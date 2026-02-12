/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Стресс-тест AVX (256-бит, single-precision). Нагружает FP-конвейеры и логическую часть:
    /// FMA/Add/Mul/Reciprocal/ReciprocalSqrt/Min/Max/Permute/Permute2x128/Compare/And/Or/Xor/AndNot + MoveMask.
    /// Память трогаем минимально: один Load/Store на множество микро-операций.
    /// </summary>
    public sealed unsafe class AvxCacheStressTest : IStressTest
    {
        // Размер рабочей области (float): > L2, чтобы изредка трогать кэш, но не упираться в память.
        private const int Length = 1 << 17;  // 131072 элементов ≈ 512 KiB
        private const int VecWidth = 8;        // Vector256<float>.Count
        private const int MicroSteps = 24;       // «глубина» регистровой обработки на один загруженный вектор

        private float[]? _a;
        private float[]? _b;

        private bool _passed;
        private float _probe;
        private int _runId;

        /// <summary>Основная AVX-нагрузка.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void RunTest()
        {
            // Если AVX недоступен - выходим «зелёным» (или можно бросать исключение, по желанию).
            if (!Avx.IsSupported)
            {
                _passed = true;
                return;
            }

            EnsureBuffers();

            fixed (float* pA = _a, pB = _b)
            {
                // Векторные константы
                Vector256<float> vEps = Vector256.Create(1e-8f);
                Vector256<float> vScale = Vector256.Create(0.9999993f);
                Vector256<float> vBias1 = Vector256.Create(0.6180339f);
                Vector256<float> vBias2 = Vector256.Create(1.4142135f);
                Vector256<float> vBias3 = Vector256.Create(1.7320508f);
                Vector256<float> vHalf = Vector256.Create(0.5f);
                Vector256<float> vOne = Vector256.Create(1.0f);
                Vector256<float> vMaxCap = Vector256.Create(1e20f);
                Vector256<float> vMinCap = Vector256.Create(-1e20f);
                Vector256<float> vSign = Vector256.Create(-0.0f); // для |x|: AndNot(-0.0f, x)

                // «битовый след» - чтобы результаты действительно «жили» и не были выкинуты JIT'ом
                Vector256<float> vChk = Vector256.Create(0.0f);

                int n = Length - (Length % VecWidth);

                // Независимые аккумуляторы (ILP)
                Vector256<float> acc0 = Vector256.Create(0.123f);
                Vector256<float> acc1 = Vector256.Create(0.456f);
                Vector256<float> acc2 = Vector256.Create(0.789f);
                Vector256<float> acc3 = Vector256.Create(1.012f);

                // Сдвиг фазы, чтобы между прогонами не попадать в те же 8-элементные окна
                int phase = unchecked(++_runId * 33) & (n - VecWidth);

                // PRNG для масок (xorshift32); MoveMask-след складываем в maskAcc
                uint prng = unchecked((uint)(0xC0FFEE ^ _runId * 0x9E3779B9u));
                int maskAcc = 0;

                for (int i = 0; i < n; i += VecWidth)
                {
                    // Загружаем два вектора; далее десятки регистровых операций без памяти
                    Vector256<float> va = Avx.LoadVector256(pA + ((i + phase) & (n - VecWidth)));
                    Vector256<float> vb = Avx.LoadVector256(pB + i);

                    Vector256<float> x = va;
                    Vector256<float> y = vb;

                    unchecked
                    {
                        for (int step = 0; step < MicroSteps; step++)
                        {
                            // ===== арифметическое ядро =====
                            // 1) x = x*(y + bias1) + bias2  (с FMA при наличии)
                            Vector256<float> tAdd = Avx.Add(y, vBias1);
                            x = Fma.IsSupported
                                ? Fma.MultiplyAdd(x, tAdd, vBias2)
                                : Avx.Add(Avx.Multiply(x, tAdd), vBias2);

                            // 2) нормализация x: x = x * rcp(|x| + eps)
                            Vector256<float> absX = Avx.AndNot(vSign, x); // |x| - сбросить знаковый бит
                            Vector256<float> den = Avx.Add(absX, vEps);
                            Vector256<float> rcp = Avx.Reciprocal(den);
                            x = Avx.Multiply(x, rcp);

                            // 3) y через rsqrt: y = rsqrt(y*y + 0.5 + eps) * (y + bias3)
                            Vector256<float> y2 = Avx.Multiply(y, y);
                            Vector256<float> y2b = Avx.Add(y2, vHalf);
                            Vector256<float> inv = Avx.ReciprocalSqrt(Avx.Add(y2b, vEps));
                            y = Avx.Multiply(inv, Avx.Add(y, vBias3));

                            // 4) перестановки lane'ов - нагрузка на permute/permute2x128
                            //   - Перестановка внутри 128-бит половин (imm8 шаблоны)
                            Vector256<float> px = Avx.Permute(x, 0b_10_01_00_11); // произвольный шаблон
                            Vector256<float> py = Avx.Permute(y, 0b_01_00_11_10);
                            //   - Перестановка между 128-бит половинами
                            Vector256<float> x2 = Avx.Permute2x128(px, py, 0b_00_10_01_11); // mix low/high
                            Vector256<float> y2p = Avx.Permute2x128(py, px, 0b_01_00_11_00);
                            x = Avx.Add(x2, vScale);
                            y = Avx.Subtract(y2p, vScale);

                            // 5) сравнения + логические маски (нагрузка на compare/logic)
                            Vector256<float> mPos = Avx.Compare(x, Vector256<float>.Zero, FloatComparisonMode.OrderedGreaterThanNonSignaling);
                            Vector256<float> yq = Avx.Multiply(y, y);
                            Vector256<float> mBig = Avx.Compare(yq, vOne, FloatComparisonMode.OrderedLessThanNonSignaling);
                            Vector256<float> mix1 = Avx.And(mPos, x);
                            Vector256<float> mix2 = Avx.AndNot(mBig, y);
                            Vector256<float> mix = Avx.Or(mix1, mix2);
                            x = Avx.Xor(x, mix);
                            y = Avx.Xor(y, mix1);

                            // 6) ограничение диапазона (min/max), чтобы исключить уходы в NaN/Inf
                            x = Avx.Min(Avx.Max(x, vMinCap), vMaxCap);
                            y = Avx.Min(Avx.Max(y, vMinCap), vMaxCap);

                            // ===== фаза масок: MoveMask + бленд =====
                            Vector256<float> m = NextMask256(ref prng); // 0xFFFFFFFF/0x00000000 per lane
                            int mm = Avx.MoveMask(m);                   // 8-битовая маска знаков
                            maskAcc ^= (mm << (step & 7)) ^ (mm * 0x45D9F3B);

                            // BLENDVPS (эмуляция): sel = (x & m) | (~m & y)
                            Vector256<float> sel = Avx.Or(Avx.And(x, m), Avx.AndNot(m, y));
                            // Вмешиваем «селект» в рабочие регистры, чтобы маска реально влияла на поток
                            x = Avx.Add(x, sel);
                            y = Avx.Subtract(y, sel);

                            // 7) независимые аккумуляторы (ILP)
                            acc0 = Avx.Add(acc0, Avx.Multiply(x, vScale));
                            acc1 = Avx.Subtract(acc1, Avx.Multiply(y, vScale));
                            acc2 = Avx.Xor(acc2, Avx.Add(x, y));
                            acc3 = Avx.Or(acc3, Avx.And(x, y));
                        }
                    }

                    // Небольшая регистровая свёртка и запись назад
                    Vector256<float> outV = Avx.Add(x, y);
                    vChk = Avx.Xor(vChk, Avx.Xor(outV, Avx.Add(acc0, acc1)));
                    Avx.Store(pA + i, outV);
                }

                // Финальная редукция в скаляр (без лишних зависимостей от ISA)
                Span<float> tmp = stackalloc float[VecWidth];
                fixed (float* pt = tmp)
                {
                    Avx.Store(pt, vChk);
                    _probe = tmp[0] + tmp[7] * 1e-6f + (maskAcc & 0xFFFF) * 1e-7f;
                }
            }

            _passed = IsFinite(_probe) && ProbeArrayFinite(_a!);
        }

        public void Validate()
        {
            if (!_passed || !IsFinite(_probe))
                throw new Exception("Ошибка AVX: получены некорректные (NaN/Inf) значения.");
        }

        // ========================= Вспомогательное =========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureBuffers()
        {
            if (_a is null || _a.Length != Length)
            {
                _a = new float[Length];
                _b = new float[Length];

                // Детерминированная инициализация, без ветвлений
                for (int i = 0; i < Length; i++)
                {
                    _a[i] = 0.001f * (i + 1);
                    _b![i] = 0.001f * (Length - i) + 0.5f;
                }
            }
        }

        /// <summary>
        /// Порождает 256-битную маску: на каждом lane либо все биты 1, либо 0.
        /// Маска представлена как Vector256&lt;float&gt; (битовая интерпретация).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> NextMask256(ref uint s)
        {
            // xorshift32 (быстро и детерминированно)
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;

            // 8 младших бит 8 lane-ов
            int m0 = ((s >> 0) & 1) != 0 ? -1 : 0;
            int m1 = ((s >> 1) & 1) != 0 ? -1 : 0;
            int m2 = ((s >> 2) & 1) != 0 ? -1 : 0;
            int m3 = ((s >> 3) & 1) != 0 ? -1 : 0;
            int m4 = ((s >> 4) & 1) != 0 ? -1 : 0;
            int m5 = ((s >> 5) & 1) != 0 ? -1 : 0;
            int m6 = ((s >> 6) & 1) != 0 ? -1 : 0;
            int m7 = ((s >> 7) & 1) != 0 ? -1 : 0;

            var vi = Vector256.Create(m0, m1, m2, m3, m4, m5, m6, m7);
            return vi.AsSingle(); // реинтерпретация int float, ISA не требует
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ProbeArrayFinite(float[] a)
        {
            int n = a.Length;
            int i0 = 0;
            int i1 = n >> 3;
            int i2 = n >> 2;
            int i3 = (n * 5) >> 3;
            int i4 = n - 1;

            return IsFinite(a[i0]) && IsFinite(a[i1]) && IsFinite(a[i2]) &&
                   IsFinite(a[i3]) && IsFinite(a[i4]);
        }
    }
}

/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using BURST.Types.Base;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Стресс-тест SSE (single-precision, 128-бит).
    /// Нагружает FP-векторные блоки и логические ALU за счёт:
    /// - плотных цепочек Add/Mul/Reciprocal/ReciprocalSqrt/Sqrt/Min/Max/Shuffle/Compare/And/Or/Xor/AndNot;
    /// - генерации псевдослучайных масок на каждом микрошаге, вызовов <see cref="Sse.MoveMask"/> и «бленда»;
    /// - высокой ILP (несколько независимых аккумуляторов), минимум обращений к памяти.
    /// </summary>
    public sealed unsafe class SseCacheStressTest : IStressTest
    {
        private const int Length = 1 << 16; // 65_536 элементов (≈256 KiB)
        private const int VecWidth = 4;       // Vector128<float>.Count
        private const int MicroSteps = 16;     // микрошагов на один загруженный вектор

        private readonly object _initLock = new object();


        private float[]? _a;
        private float[]? _b;

        private bool _passed;
        private float _probe;
        private int _runId;

        public void RunTest()
        {
            if (!Sse.IsSupported)
            {
                _passed = true;
                return;
            }

            EnsureBuffers();

            fixed (float* pA = _a, pB = _b)
            {
                // Векторные константы
                Vector128<float> vEps = Vector128.Create(1e-8f);
                Vector128<float> vScale = Vector128.Create(0.9999993f);
                Vector128<float> vBias1 = Vector128.Create(0.6180339f);
                Vector128<float> vBias2 = Vector128.Create(1.4142135f);
                Vector128<float> vBias3 = Vector128.Create(1.7320508f);
                Vector128<float> vHalf = Vector128.Create(0.5f);
                Vector128<float> vOne = Vector128.Create(1.0f);
                Vector128<float> vMaxCap = Vector128.Create(1e20f);
                Vector128<float> vMinCap = Vector128.Create(-1e20f);
                Vector128<float> vSign = Vector128.Create(-0.0f); // для |x| = AndNot(-0.0f, x)

                Vector128<float> vChk = Vector128.Create(0.0f);

                int n = Length - (Length % VecWidth);

                // Независимые аккумуляторы (ILP)
                Vector128<float> acc0 = Vector128.Create(0.123f);
                Vector128<float> acc1 = Vector128.Create(0.456f);
                Vector128<float> acc2 = Vector128.Create(0.789f);
                Vector128<float> acc3 = Vector128.Create(1.012f);

                // Смещаем фазу, чтобы избегать одинаковых паттернов между прогонами
                int phase = unchecked(++_runId * 17) & (n - VecWidth);

                // PRNG для масок (xorshift32) - детерминированный и быстрый
                uint prng = unchecked((uint)(0xC0FFEE ^ _runId * 0x9E3779B9));

                // Аккумулятор масок, чтобы их работа «жила»
                int maskAcc = 0;

                for (int i = 0; i < n; i += VecWidth)
                {
                    Vector128<float> va = Sse.LoadVector128(pA + ((i + phase) & (n - VecWidth)));
                    Vector128<float> vb = Sse.LoadVector128(pB + i);

                    Vector128<float> x = va;
                    Vector128<float> y = vb;

                    unchecked
                    {
                        for (int step = 0; step < MicroSteps; step++)
                        {
                            // ---- арифметическое ядро ----
                            // 1) x = x*(y + bias1) + bias2
                            Vector128<float> tmpAdd = Sse.Add(y, vBias1);
                            Vector128<float> tmpMul = Sse.Multiply(x, tmpAdd);
                            x = Sse.Add(tmpMul, vBias2);

                            // 2) x = x * rcp(|x| + eps)
                            Vector128<float> absX = Sse.AndNot(vSign, x);
                            Vector128<float> den = Sse.Add(absX, vEps);
                            Vector128<float> rcp = Sse.Reciprocal(den);
                            x = Sse.Multiply(x, rcp);

                            // 3) y = rsqrt(y*y + 0.5 + eps) * (y + bias3)
                            Vector128<float> y2 = Sse.Multiply(y, y);
                            Vector128<float> y2b = Sse.Add(y2, vHalf);
                            Vector128<float> inv = Sse.ReciprocalSqrt(Sse.Add(y2b, vEps));
                            y = Sse.Multiply(inv, Sse.Add(y, vBias3));

                            // 4) Перемешивания (нагрузка на shuffle)
                            Vector128<float> xs = Sse.Shuffle(x, y, 0b_11_00_10_01);
                            Vector128<float> ys = Sse.Shuffle(y, x, 0b_01_10_00_11);
                            x = Sse.Add(xs, vScale);
                            y = Sse.Subtract(ys, vScale);

                            // 5) Сравнения + логика
                            Vector128<float> mPos = Sse.CompareLessThan(Vector128<float>.Zero, x);   // x > 0
                            Vector128<float> mBig = Sse.CompareLessThan(Sse.Multiply(y, y), vOne);   // y^2 < 1
                            Vector128<float> mix1 = Sse.And(mPos, x);
                            Vector128<float> mix2 = Sse.AndNot(mBig, y);
                            Vector128<float> mix = Sse.Or(mix1, mix2);
                            x = Sse.Add(x, mix);      // мягкая «подмешка» значений под маской
                            y = Sse.Subtract(y, mix1);

                            // 6) Ограничение диапазона
                            x = Sse.Min(Sse.Max(x, vMinCap), vMaxCap);
                            y = Sse.Min(Sse.Max(y, vMinCap), vMaxCap);

                            // ---- Фаза масок: генерация, MoveMask, «бленд» ----
                            Vector128<float> m = NextMask(ref prng);     // 0xFFFFFFFF/0x00000000 в lane'ах
                            int mm = Sse.MoveMask(m);                    // 4-битовый снэпшот знаков lane'ов
                            maskAcc ^= (mm << (step & 7)) ^ (mm * 0x45D9F3B);

                            // Эмуляция BLENDVPS на SSE: (x & m) | (~m & y)
                            Vector128<float> sel = Sse.Or(Sse.And(x, m), Sse.AndNot(m, y));
                            // Слегка вмешиваемся в рабочие регистры, чтобы маска реально влияла на поток
                            x = Sse.Add(x, sel);
                            y = Sse.Subtract(y, sel);

                            // 7) Независимые аккумуляторы (ILP)
                            acc0 = Sse.Add(acc0, Sse.Multiply(x, vScale));
                            acc1 = Sse.Subtract(acc1, Sse.Multiply(y, vScale));
                            var i_acc2 = Sse2.Xor(acc2.AsInt32(), Sse2.Xor(x.AsInt32(), y.AsInt32()));
                            acc2 = i_acc2.AsSingle();
                            acc3 = Sse.Or(acc3, Sse.And(x, y));
                        }
                    }

                    Vector128<float> outV = Sse.Add(x, y);
                    var i_chk = Sse2.Xor(vChk.AsInt32(),
                    Sse2.Xor(outV.AsInt32(), Sse2.Add(acc0, acc1).AsInt32()));
                    vChk = i_chk.AsSingle();
                    Sse.Store(pA + i, outV);
                }

                // Финальная редукция
                var i_vChk = vChk.AsInt32();
                var i_r1 = Sse.Shuffle(vChk, vChk, 0b_10_11_00_01).AsInt32();
                var i_r2 = Sse2.Xor(i_vChk, i_r1);
                var i_r3 = Sse.Shuffle(i_r2.AsSingle(), i_r2.AsSingle(), 0b_00_00_11_10).AsInt32();
                var i_r4 = Sse2.Xor(i_r2, i_r3);

                // горизонтальная XOR-свёртка в скаляр int
                Span<int> tmp = stackalloc int[4];
                unsafe { fixed (int* pt = tmp) Sse2.Store(pt, i_r4); }
                int fold = tmp[0] ^ tmp[1] ^ tmp[2] ^ tmp[3];

                // превращаем в гарантированно конечный float (мантисса 23 бита, экспонента = 0x3F800000 ⇒ ~[1,2))
                _probe = BitConverter.Int32BitsToSingle((fold & 0x007FFFFF) | 0x3F800000);
            }

            _passed = IsFinite(_probe) && ProbeArrayFinite(_a!);
        }

        public void Validate()
        {
            if (!_passed || !IsFinite(_probe))
                throw new Exception("Ошибка SSE: получены некорректные (NaN/Inf) значения.");
        }

        // -------------------------- Генерация масок и утилиты --------------------------

        /// <summary>
        /// Генерирует 128-битную маску из 4 битов PRNG:
        /// lane = 0xFFFFFFFF (все биты 1) или 0x00000000, представлена как Vector128&lt;float&gt; (битовое представление).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> NextMask(ref uint s)
        {
            // xorshift32
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;

            // Используем 4 младших бита: на каждый lane - 0 (0x00000000) или -1 (0xFFFFFFFF)
            int m0 = ((s >> 0) & 1) != 0 ? -1 : 0;
            int m1 = ((s >> 1) & 1) != 0 ? -1 : 0;
            int m2 = ((s >> 2) & 1) != 0 ? -1 : 0;
            int m3 = ((s >> 3) & 1) != 0 ? -1 : 0;

            // Собираем как int-вектор, затем битово реинтерпретируем в float
            Vector128<int> vi = Vector128.Create(m0, m1, m2, m3);
            return vi.AsSingle(); // reinterpret cast, без требований к ISA
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureBuffers()
        {
            if (_a is not null && _a.Length == Length &&
                _b is not null && _b.Length == Length)
                return;

            lock (_initLock)
            {
                // Повторная проверка под замком
                if (_a is null || _a.Length != Length)
                    _a = new float[Length];
                if (_b is null || _b.Length != Length)
                    _b = new float[Length];

                // Инициализируем ОТДЕЛЬНО каждый буфер - вне зависимости от того, был ли он только что создан.
                // Это снимает любые «полуинициализированные» состояния при гонках.
                for (int i = 0; i < Length; i++)
                {
                    _a[i] = 0.001f * (i + 1);
                    _b[i] = 0.001f * (Length - i) + 0.5f;
                }
            }
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

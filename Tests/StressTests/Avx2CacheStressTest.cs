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
    /// AVX2 стресс (integer SIMD, 256-бит) с двумя фазами:
    /// 1) регистрово-вычислительная (минимум памяти, максимум ALU/permute/logic),
    /// 2) кэш-стресс с псевдослучайной выборкой через Gather (нерегулярные чтения).
    /// </summary>
    public sealed unsafe class Avx2CacheStressTest : IStressTest
    {
        private const int Length = 1 << 17;    // 131_072 int (~512 KiB)
        private const int VecWidth = 8;          // Vector256<int>.Count
        private const int MicroSteps = 24;         // глубина регистровой обработки
        private const int GatherVecSteps = 1 << 14;    // ~16K gather-шагов

        private int[]? _a;
        private int[]? _b;

        private bool _passed;
        private int _probe;
        private int _runId;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void RunTest()
        {
            if (!Avx2.IsSupported)
            {
                _passed = true;
                return;
            }

            EnsureBuffers();

            fixed (int* pA = _a, pB = _b)
            {
                // Константы
                Vector256<int> vOne = Vector256.Create(1);
                Vector256<int> vMask31 = Vector256.Create(31);
                Vector256<int> vMixC1 = Vector256.Create(unchecked((int)0x9E3779B9));
                Vector256<int> vMixC2 = Vector256.Create(unchecked((int)0x85EBCA6B));
                Vector256<int> vMixC3 = Vector256.Create(unchecked((int)0xC2B2AE35));

                Vector256<int> vChk = Vector256.Create(0);
                Vector256<int> acc0 = Vector256.Create(123456789);
                Vector256<int> acc1 = Vector256.Create(unchecked((int)0xA5A5A5A5));
                Vector256<int> acc2 = Vector256.Create(0x3C3C3C3C);
                Vector256<int> acc3 = Vector256.Create(unchecked((int)0x96969696));

                int n = Length - (Length % VecWidth);
                int phase = unchecked(++_runId * 33) & (n - VecWidth);
                uint prng = unchecked((uint)(0xC0FFEE ^ _runId * 0x9E3779B9u));
                int maskAcc = 0;

                // ===================== ФАЗА 1: регистрово-вычислительная =====================
                for (int i = 0; i < n; i += VecWidth)
                {
                    Vector256<int> x = Avx.LoadVector256(pA + ((i + phase) & (n - VecWidth)));
                    Vector256<int> y = Avx.LoadVector256(pB + i);

                    unchecked
                    {
                        for (int step = 0; step < MicroSteps; step++)
                        {
                            // 1) Mullo + Add/Sub
                            Vector256<int> yC = Avx2.Add(y, vMixC1);
                            x = Avx2.Add(Avx2.MultiplyLow(x, yC), vMixC2);
                            Vector256<int> xC = Avx2.Add(x, vMixC3);
                            y = Avx2.Subtract(Avx2.MultiplyLow(y, xC), vMixC1);

                            // 2) Var-shift per lane (через 2×128-бит помощники)
                            Vector256<int> sh = NextShiftVec(ref prng);                 // 0..31
                            Vector256<int> sh2 = Avx2.And(Avx2.Add(sh, vOne), vMask31);
                            Vector256<int> xl = ShiftLeftLogicalVariable256(x, sh);
                            Vector256<int> yr = ShiftRightLogicalVariable256(y, sh2);
                            x = Avx2.Xor(x, xl);
                            y = Avx2.Xor(y, yr);

                            // 3) Compare + BlendVariable (две маски)
                            Vector256<int> mGT = Avx2.CompareGreaterThan(x, y);
                            Vector256<int> randM = NextMask256(ref prng);
                            Vector256<int> sel1 = Avx2.BlendVariable(x, y, mGT);   // mGT ? y : x
                            Vector256<int> sel2 = Avx2.BlendVariable(y, x, randM); // randM ? x : y
                            x = Avx2.Xor(x, sel1);
                            y = Avx2.Or(y, sel2);

                            // 4) Перестановки
                            Vector256<int> perm = NextPermute8(ref prng);
                            Vector256<int> px = Avx2.PermuteVar8x32(x, perm);
                            Vector256<int> py = Avx2.PermuteVar8x32(y, perm);
                            Vector256<int> mix = Avx2.Permute2x128(px, py, 0b_00_10_01_11);
                            x = Avx2.Add(mix, xC);
                            y = Avx2.Subtract(py, yC);

                            // 5) Байтовые трюки (на 128-бит половинах): AlignRight + PSADBW
                            Vector128<byte> xbLo = Avx2.ExtractVector128(x.AsByte(), 0);
                            Vector128<byte> xbHi = Avx2.ExtractVector128(x.AsByte(), 1);
                            Vector128<byte> ybLo = Avx2.ExtractVector128(y.AsByte(), 0);
                            Vector128<byte> ybHi = Avx2.ExtractVector128(y.AsByte(), 1);

                            Vector128<byte> arLo = Avx2.AlignRight(xbLo, ybLo, 7);
                            Vector128<byte> arHi = Avx2.AlignRight(xbHi, ybHi, 7);

                            // SumAbsoluteDifferences возвращает Vector128<ushort>
                            Vector128<ushort> sadLo = Avx2.SumAbsoluteDifferences(xbLo, arLo);
                            Vector128<ushort> sadHi = Avx2.SumAbsoluteDifferences(xbHi, arHi);

                            // Собираем в 256 и XOR с acc2 (через reinterpret)
                            Vector256<ushort> sad256 = Vector256<ushort>.Zero;
                            sad256 = Avx.InsertVector128(sad256, sadLo, 0);
                            sad256 = Avx.InsertVector128(sad256, sadHi, 1);
                            acc2 = Avx2.Xor(acc2, sad256.AsInt32());

                            // 6) MoveMask след
                            int mm1 = Avx2.MoveMask(mGT.AsSByte());
                            int mm2 = Avx2.MoveMask(randM.AsSByte());
                            maskAcc ^= (mm1 * 0x45D9F3B) ^ (mm2 << (step & 7));

                            // 7) Независимые аккумуляторы
                            acc0 = Avx2.Add(acc0, Avx2.Xor(x, vMixC1));
                            acc1 = Avx2.Subtract(acc1, Avx2.Or(y, vMixC2));
                            acc3 = Avx2.Xor(acc3, Avx2.And(x, y));
                        }
                    }

                    Vector256<int> outV = Avx2.Xor(x, y);
                    vChk = Avx2.Xor(vChk, Avx2.Xor(outV, Avx2.Add(acc0, acc1)));
                    Avx.Store(pA + i, outV);
                }

                // ===================== ФАЗА 2: кэш-стресс через GATHER =====================
                for (int step = 0; step < GatherVecSteps; step++)
                {
                    Vector256<int> idxA = NextIndexVec(ref prng, n);
                    Vector256<int> idxB = NextIndexVec(ref prng, n);

                    Vector256<int> gx = Avx2.GatherVector256(pA, idxA, 4);
                    Vector256<int> gy = Avx2.GatherVector256(pB, idxB, 4);

                    Vector256<int> sh = NextShiftVec(ref prng);
                    Vector256<int> gl = ShiftLeftLogicalVariable256(gx, Avx2.And(sh, vMask31));
                    Vector256<int> gr = ShiftRightLogicalVariable256(gy, Avx2.And(Avx2.Add(sh, vOne), vMask31));

                    Vector256<int> cmp = Avx2.CompareGreaterThan(gl, gr);
                    Vector256<int> mix = Avx2.Xor(Avx2.MultiplyLow(gl, vMixC1), Avx2.MultiplyLow(gr, vMixC2));
                    Vector256<int> sel = Avx2.BlendVariable(mix, Avx2.Xor(gx, gy), cmp);

                    Vector256<int> perm = NextPermute8(ref prng);
                    Vector256<int> rez = Avx2.PermuteVar8x32(sel, perm);
                    vChk = Avx2.Xor(vChk, rez);

                    int mm = Avx2.MoveMask(cmp.AsSByte());
                    maskAcc ^= (mm * 0x27D4EB2D) ^ step;

                    int outPos = (step * VecWidth) & (n - VecWidth);
                    Avx.Store(pA + outPos, rez);
                }

                // Финальный редьюс
                Span<int> tmp = stackalloc int[VecWidth];
                fixed (int* pt = tmp)
                {
                    Avx.Store(pt, vChk);
                    int fold = tmp[0] ^ tmp[1] ^ tmp[2] ^ tmp[3] ^ tmp[4] ^ tmp[5] ^ tmp[6] ^ tmp[7];
                    _probe = fold ^ unchecked((int)(maskAcc * 0x9E3779B9u));

                }
            }

            _passed = (_probe != 0) && ProbeArray(_a!);
        }

        public void Validate()
        {
            if (!_passed || _probe == 0)
                throw new Exception("Ошибка AVX2: недопустимые/тривиальные значения после gather.");
        }

        // ========================= ПОМОЩНИКИ ДЛЯ 256-БИТ ПЕРЕМЕННЫХ СДВИГОВ =========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> ShiftLeftLogicalVariable256(Vector256<int> value, Vector256<int> counts)
        {
            // AVX2 API для переменного сдвига принимает Vector128<int> / Vector128<uint>.
            var vLo = Avx2.ExtractVector128(value, 0);
            var vHi = Avx2.ExtractVector128(value, 1);
            var cLo = Avx2.ExtractVector128(counts.AsUInt32(), 0);
            var cHi = Avx2.ExtractVector128(counts.AsUInt32(), 1);

            Vector128<int> rLo = Avx2.ShiftLeftLogicalVariable(vLo, cLo);
            Vector128<int> rHi = Avx2.ShiftLeftLogicalVariable(vHi, cHi);

            Vector256<int> res = Vector256<int>.Zero;
            res = Avx.InsertVector128(res, rLo, 0);
            res = Avx.InsertVector128(res, rHi, 1);
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> ShiftRightLogicalVariable256(Vector256<int> value, Vector256<int> counts)
        {
            var vLo = Avx2.ExtractVector128(value, 0);
            var vHi = Avx2.ExtractVector128(value, 1);
            var cLo = Avx2.ExtractVector128(counts.AsUInt32(), 0);
            var cHi = Avx2.ExtractVector128(counts.AsUInt32(), 1);

            Vector128<int> rLo = Avx2.ShiftRightLogicalVariable(vLo, cLo);
            Vector128<int> rHi = Avx2.ShiftRightLogicalVariable(vHi, cHi);

            Vector256<int> res = Vector256<int>.Zero;
            res = Avx.InsertVector128(res, rLo, 0);
            res = Avx.InsertVector128(res, rHi, 1);
            return res;
        }

        // ========================= ПРОЧИЕ ВСПОМОГАТЕЛЬНЫЕ =========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureBuffers()
        {
            if (_a is null || _a.Length != Length)
            {
                _a = new int[Length];
                _b = new int[Length];
                for (int i = 0; i < Length; i++)
                {
                    int x = unchecked(i * 1103515245 + 12345);
                    _a[i] = x ^ (x << 7) ^ (x >> 9);
                    int y = unchecked((int)((Length - i) * 1103515245u + 0xA5A5A5A5u));
                    _b![i] = y ^ (y << 11) ^ (y >> 5);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> NextMask256(ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            int m0 = ((s >> 0) & 1) != 0 ? -1 : 0;
            int m1 = ((s >> 1) & 1) != 0 ? -1 : 0;
            int m2 = ((s >> 2) & 1) != 0 ? -1 : 0;
            int m3 = ((s >> 3) & 1) != 0 ? -1 : 0;
            int m4 = ((s >> 4) & 1) != 0 ? -1 : 0;
            int m5 = ((s >> 5) & 1) != 0 ? -1 : 0;
            int m6 = ((s >> 6) & 1) != 0 ? -1 : 0;
            int m7 = ((s >> 7) & 1) != 0 ? -1 : 0;
            return Vector256.Create(m0, m1, m2, m3, m4, m5, m6, m7);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> NextShiftVec(ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            int c0 = (int)(s & 31);
            int c1 = (int)((s >> 5) & 31);
            int c2 = (int)((s >> 10) & 31);
            int c3 = (int)((s >> 15) & 31);
            int c4 = (int)((s >> 20) & 31);
            int c5 = (int)((s >> 25) & 31);
            int c6 = (int)(((s >> 30) | (s << 2)) & 31);
            int c7 = (int)(((s >> 28) | (s << 4)) & 31);
            return Vector256.Create(c0, c1, c2, c3, c4, c5, c6, c7);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> NextIndexVec(ref uint s, int n)
        {
            uint r0 = Next(ref s), r1 = Next(ref s), r2 = Next(ref s), r3 = Next(ref s);
            uint r4 = Next(ref s), r5 = Next(ref s), r6 = Next(ref s), r7 = Next(ref s);

            int i0 = FastRange(r0, n);
            int i1 = FastRange(r1, n);
            int i2 = FastRange(r2, n);
            int i3 = FastRange(r3, n);
            int i4 = FastRange(r4, n);
            int i5 = FastRange(r5, n);
            int i6 = FastRange(r6, n);
            int i7 = FastRange(r7, n);

            return Vector256.Create(i0, i1, i2, i3, i4, i5, i6, i7);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint Next(ref uint x)
            { x ^= x << 13; x ^= x >> 17; x ^= x << 5; return x; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int FastRange(uint v, int range) => (int)((ulong)v * (uint)range >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> NextPermute8(ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            int p0 = (int)((s >> 0) & 7);
            int p1 = (int)((s >> 3) & 7);
            int p2 = (int)((s >> 6) & 7);
            int p3 = (int)((s >> 9) & 7);
            int p4 = (int)((s >> 12) & 7);
            int p5 = (int)((s >> 15) & 7);
            int p6 = (int)((s >> 18) & 7);
            int p7 = (int)((s >> 21) & 7);
            return Vector256.Create(p0, p1, p2, p3, p4, p5, p6, p7);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ProbeArray(int[] a)
        {
            int n = a.Length;
            int i0 = 0;
            int i1 = n >> 3;
            int i2 = n >> 2;
            int i3 = (n * 5) >> 3;
            int i4 = n - 1;
            int x = a[i0] ^ a[i1] ^ a[i2] ^ a[i3] ^ a[i4];
            return x != 0;
        }
    }
}

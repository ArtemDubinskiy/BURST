/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System;
using System.Buffers.Binary;              // BinaryPrimitives
using System.Runtime.CompilerServices;     // MethodImplOptions
using System.Security.Cryptography;        // SHA256.HashData / SHA512.HashData
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Тест хеширования: интенсивные SHA-256/512 без аллокаций и с обратной связью состояния.
    /// <para>
    /// Цели:
    /// <list type="bullet">
    ///   <item><description>Нагрузить криптографические примитивы (SHA-256/512) - integer/битовые конвейеры CPU.</description></item>
    ///   <item><description>Исключить мусор GC: использовать <c>stackalloc</c> и Span-API (<c>HashData</c>).</description></item>
    ///   <item><description>Высокий ILP: в сообщении смешиваются несколько независимых компонентов.</description></item>
    ///   <item><description>Дешёвая внутренняя самопроверка: периодическая повторная хеш-проверка без аллокаций.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class HashingStressTest : IStressTest
    {
        // «Вес» одного внешнего прогона. Общую длительность задаёт инфраструктура (--cycles).
        private const int InnerIterations = 200_000;

        // Размер сообщения на итерацию (байт). Достаточно, чтобы пройти несколько внутренних раундов SHA.
        private const int MsgSize = 128;

        // Состояние между итерациями - 32 байта (как блок для SHA-256), живёт в куче один раз.
        private readonly byte[] _state = new byte[32]
        {
            // Детерминированные «семена» (можно любые константы)
            0x6D,0x5A,0x56,0xFA, 0x27,0xD3,0x9B,0x59,
            0x01,0x83,0xA2,0xE6, 0x8C,0x85,0x0B,0xC5,
            0x3A,0xD7,0xC2,0x32, 0x49,0xAC,0x47,0x1F,
            0xFF,0x0C,0x9E,0x37, 0x79,0xB9,0x94,0xD0
        };

        // «Зонд» для лёгкой проверки корректности.
        private uint _probe;
        private bool _passed;

        /// <summary>
        /// Основная нагрузка: в каждом шаге формируется сообщение (state||counter||fill),
        /// считается SHA-256 и SHA-512; результаты смешиваются и фидбэчатся обратно в state.
        /// Каждые 1024 итерации выполняется повторная проверка того же входа.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void RunTest()
        {
            Span<byte> state = _state;                     // 32 байта
            Span<byte> msg = stackalloc byte[MsgSize];   // 128 байт
            Span<byte> h256 = stackalloc byte[32];        // 32
            Span<byte> h512 = stackalloc byte[64];        // 64
            Span<byte> fold = stackalloc byte[32];        // свёртка SHA-512 -> 32 (xor половинок)

            // Локальные регистровые генераторы для наполнения сообщения (детерминированно, integer-only).
            ulong s0 = 0x9E3779B97F4A7C15ul;
            ulong s1 = 0xC2B2AE3D27D4EB4Ful;

            unchecked
            {
                for (int i = 0; i < InnerIterations; i++)
                {
                    // -------------------- формирование сообщения --------------------
                    // [0..31]  = текущее состояние
                    state.CopyTo(msg);

                    // [32..39] = счетчик i (LE)
                    BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(32, 8), i);

                    // [40..127] = «шум» из xorshift128+ (чтобы сообщение менялось каждый раз)
                    FillWithXorShift(msg.Slice(40), ref s0, ref s1);

                    // -------------------- SHA-256 + SHA-512 --------------------
                    // Без аллокаций: статические методы с ReadOnlySpan->Span
                    SHA256.HashData(msg, h256);
                    SHA512.HashData(msg, h512);

                    // Сворачиваем SHA-512 до 32 байт (xor половинок) - усиливает перемешивание.
                    for (int k = 0; k < 32; k++)
                        fold[k] = (byte)(h512[k] ^ h512[k + 32]);

                    // Обратная связь: новое состояние = state ^ h256 ^ fold
                    for (int k = 0; k < 32; k++)
                        state[k] ^= (byte)(h256[k] ^ fold[k]);

                    // -------------------- периодическая самопроверка --------------------
                    // Каждые 1024 итерации пересчитываем SHA-256 того же сообщения и сравниваем.
                    if ((i & 1023) == 0)
                    {
                        Span<byte> check = stackalloc byte[32];
                        SHA256.HashData(msg, check);
                        if (!h256.SequenceEqual(check))
                        {
                            _passed = false;
                            return;
                        }
                    }
                }
            }

            // Лёгкий финальный «зонд»: CRC-подобная свёртка state в 32-бит (без аллокаций).
            _probe = Probe32(state);
            _passed = true;
        }

        /// <summary>
        /// Проверка: просто убеждаемся, что «зонд» имеет не-тривиальный вид (не 0x00000000, не 0xFFFFFFFF),
        /// и флаг корректности выставлен в горячем цикле.
        /// </summary>
        public void Validate()
        {
            if (!_passed || _probe == 0u || _probe == 0xFFFFFFFFu)
                throw new Exception("Ошибка хеширования: нарушена повторная проверка или зонд тривиален.");
        }

        // ========================= Вспомогательные низкоуровневые рутины =========================

        /// <summary>
        /// Быстрый детерминированный наполнитель xorshift128+ (integer-only, без аллокаций).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FillWithXorShift(Span<byte> dst, ref ulong s0, ref ulong s1)
        {
            int len = dst.Length & ~7;
            for (int off = 0; off < len; off += 8)
            {
                ulong r = Next(ref s0, ref s1);
                // Пишем 8 байт LE
                dst[off + 0] = (byte)r;
                dst[off + 1] = (byte)(r >> 8);
                dst[off + 2] = (byte)(r >> 16);
                dst[off + 3] = (byte)(r >> 24);
                dst[off + 4] = (byte)(r >> 32);
                dst[off + 5] = (byte)(r >> 40);
                dst[off + 6] = (byte)(r >> 48);
                dst[off + 7] = (byte)(r >> 56);
            }
            // хвост
            for (int i = len; i < dst.Length; i++)
            {
                ulong r = Next(ref s0, ref s1);
                dst[i] = (byte)r;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong Next(ref ulong s0, ref ulong s1)
            {
                ulong x = s0;
                ulong y = s1;
                s0 = y;
                x ^= x << 23;
                x ^= x >> 17;
                x ^= y ^ (y >> 26);
                s1 = x;
                return x + y;
            }
        }

        /// <summary>
        /// Лёгкая 32-бит свёртка состояния (чувствительна к порядку, без крипто-гарантий).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Probe32(ReadOnlySpan<byte> s)
        {
            uint a = 0x9E3779B9u, b = 0x85EBCA6Bu, c = 0xC2B2AE35u;
            for (int i = 0; i < s.Length; i++)
            {
                a += s[i];
                b ^= (uint)(s[i] << (i & 15));
                c = (c << 5) | (c >> 27);
                a = a * 2654435761u + b;
                b = b * 2246822519u + c;
                c = c * 3266489917u + a;
            }
            return a ^ b ^ c;
        }
    }
}

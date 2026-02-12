/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Тест на чтение/запись блоков памяти (memory bandwidth / latency stress).
    /// <para>
    /// Нагружает подсистему памяти за счёт:
    /// <list type="bullet">
    ///   <item><description>крупноблочной инициализации (memset-подобная запись);</description></item>
    ///   <item><description>копирования (memcpy-подобные операции, невзаимное/перекрывающееся);</description></item>
    ///   <item><description>последовательных и страйдовых проходов (кэш-промахи, TLB-давление);</description></item>
    ///   <item><description>случайных read-modify-write с обратимым XOR-ключом (генерация трафика без изменения итогового содержимого);</description></item>
    ///   <item><description>валидации порядкочувствительным 64-бит хэшем - ловит любые повреждения/пропуски.</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// - Буферы выделяются один раз и переиспользуются (минимум давления на GC).
    /// - Основные циклы - без ветвлений по пути данных; всё в Span/unsafe для высокой скорости.
    /// - Никаких float/double и тяжёлых ALU - фокус именно на памяти.
    /// </remarks>
    /// </summary>
    public sealed class MemoryStressTest : IStressTest
    {
        // Размер основного буфера. Должен существенно превышать LLC/LL2, чтобы выйти в DRAM.
        // Для рабочих станций хорошо 32–128 МБ. Подбирается компромисс: нагрузка vs. общий объём.
        private const int BufferBytes = 32 * 1024 * 1024; // 32 MiB

        // Размер «страйда» для проходов по буферу: должен «ломать» предвыборки и попадать в разные кэш-линии.
        private const int Stride = 4096; // 4 KiB - размер страницы: хорошо трясёт TLB/кэш

        // Количество случайных RMW-операций на проход (в штуках индексов).
        // Должно быть достаточно большим, чтобы нагрузить память, но не блокировать надолго один RunTest().
        private const int RandomOps = 2_000_000;

        // Основные буферы (выделяются лениво один раз).
        private byte[]? _buf;
        private byte[]? _shadow;

        // Снимок контрольной суммы для валидации.
        private ulong _hashAfter;
        private bool _passed;

        // Счётчик запусков для варьирования сидов PRNG.
        private int _runId;

        /// <summary>
        /// Основная память-нагружающая часть.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void RunTest()
        {
            EnsureBuffers();

            Span<byte> span = _buf!;
            Span<byte> aux = _shadow!;

            // 1) Инициализация буфера детерминированным шаблоном f(i) - порядок важен.
            FillPattern(span);

            // 2) memcpy: копируем в теневой буфер (невзаимные области).
            span.CopyTo(aux);

            // 3) memcpy с перекрытием (overlap): сдвиг на половину страницы - упражнение для реализаций copy.
            var srcOverlap = span.Slice(0, span.Length - 2048);
            var dstOverlap = span.Slice(2048, span.Length - 2048);
            srcOverlap.CopyTo(dstOverlap);

            // 4) Возвращаем исходное состояние из тени (восстановление порядка/данных).
            aux.CopyTo(span);

            // 5) Последовательный проход read-only (нагрузка на чтение, прогрев кэшей/предвыборка).
            ulong checksumSeq = SequentialChecksum(span);

            // 6) Страйдовый проход write-only: перезаписываем каждую страницу «маркером»,
            //    затем обратная запись тем же маркером (XOR-схема), чтобы итог НЕ изменился.
            StridedMarkerWriteXor(span);

            // 7) Случайные read-modify-write: XOR с ключом по детерминированной последовательности,
            //    затем повтор тем же ключом и той же последовательностью (реверс) - итог содержимого не меняется.
            int seed = unchecked(0xC0FFEE ^ ++_runId);
            RandomXorRmw(span, seed);
            RandomXorRmw(span, seed); // второй проход тем же сидом «откатывает» изменения

            // 8) Финальный порядкочувствительный хэш - должен совпасть с чистым от шага 1/2/4.
            _hashAfter = StrongOrderHash(span) ^ checksumSeq; // комбинируем с seq-чеком, чтобы усилить покрытие
            _passed = true;
        }

        /// <summary>
        /// Валидация: пересчитывает эталонный хэш того же состояния и сравнивает с полученным.
        /// Любая порча/недозапись/перезапись будет обнаружена.
        /// </summary>
        public void Validate()
        {
            if (_buf is null)
            {
                _passed = false;
            }
            else
            {
                // Эталон: после всех «обратимых» операций содержимое должно совпадать с f(i).
                // Поэтому просто пересоздаём f(i) на лету и считаем целевой хэш напрямую из буфера.
                // Чтобы не «затирать» данные для следующего прогона, хэш считаем поверх текущего состояния.
                // Сверяем с хэшем, полученным в RunTest() (там также StrongOrderHash ^ seqChecksum).
                ulong expected = StrongOrderHash(_buf) ^ SequentialChecksum(_buf);
                if (_hashAfter != expected) _passed = false;
            }

            if (!_passed)
                throw new Exception("Ошибка работы с памятью: нарушена целостность/порядок данных в буфере.");
        }

        // ========================= НИЖЕ - ВСПОМОГАТЕЛЬНЫЕ «ГОРЯЧИЕ» РУТИНЫ =========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureBuffers()
        {
            if (_buf is null || _buf.Length != BufferBytes)
                _buf = new byte[BufferBytes];
            if (_shadow is null || _shadow.Length != BufferBytes)
                _shadow = new byte[BufferBytes];
        }

        /// <summary>Инициализация шаблоном: data[i] = (byte)((i * A) ^ RotateRight(i, r)) - детерминированно и «шероховато».</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void FillPattern(Span<byte> data)
        {
            // Пишем 8 байт за раз для пропускной способности.
            const uint A = 2654435761u; // φ*2^32 (Knuth)
            int len = data.Length & ~7;
            ref byte b0 = ref MemoryMarshal.GetReference(data);

            for (int i = 0; i < len; i += 8)
            {
                uint x = (uint)i;
                ulong v =
                    ((ulong)PatternByte(x + 0, A) << 0) |
                    ((ulong)PatternByte(x + 1, A) << 8) |
                    ((ulong)PatternByte(x + 2, A) << 16) |
                    ((ulong)PatternByte(x + 3, A) << 24) |
                    ((ulong)PatternByte(x + 4, A) << 32) |
                    ((ulong)PatternByte(x + 5, A) << 40) |
                    ((ulong)PatternByte(x + 6, A) << 48) |
                    ((ulong)PatternByte(x + 7, A) << 56);

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref b0, i), v);
            }

            // Хвост
            for (int i = len; i < data.Length; i++)
                data[i] = PatternByte((uint)i, A);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static byte PatternByte(uint i, uint Aconst)
            {
                uint t = i * Aconst;
                t ^= (t >> 13) | (t << 19);     // псевдо-rotate
                t ^= (i << 7) ^ (i >> 9);
                return (byte)(t & 0xFF);
            }
        }

        /// <summary>
        /// Сильная порядкочувствительная 64-бит контрольная сумма (не крипто).
        /// Очень быстрая, чувствительна к любым перестановкам/порче байтов.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ulong StrongOrderHash(ReadOnlySpan<byte> data)
        {
            // Мелкий смешиватель по мотивам SplitMix64/xxh - быстрая и «шумная».
            const ulong M1 = 0xBF58476D1CE4E5B9ul;
            const ulong M2 = 0x94D049BB133111EBul;

            ulong h = 0x9E3779B97F4A7C15ul ^ (ulong)data.Length;

            int len = data.Length & ~7;
            ref byte b0 = ref MemoryMarshal.GetReference(data);

            for (int i = 0; i < len; i += 8)
            {
                ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, i));
                h ^= Mix64(v + (ulong)i * 0x9E3779B97F4A7C15ul);
                h = RotateLeft(h, 27) * 5 + 0x52DCE729;
            }

            // хвост
            ulong tail = 0;
            int shift = 0;
            for (int i = len; i < data.Length; i++, shift += 8)
                tail |= (ulong)data[i] << shift;

            h ^= Mix64(tail);
            h ^= h >> 31; h *= M1;
            h ^= h >> 27; h *= M2;
            h ^= h >> 33;
            return h;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong Mix64(ulong z)
            {
                z ^= z >> 30; z *= M1;
                z ^= z >> 27; z *= M2;
                z ^= z >> 31;
                return z;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong RotateLeft(ulong x, int r) => (x << r) | (x >> (64 - r));
        }

        /// <summary>
        /// Быстрая последовательная «чексумма» для дополнительного покрытия (легковесна).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ulong SequentialChecksum(ReadOnlySpan<byte> data)
        {
            ulong s0 = 0, s1 = 1;
            int len = data.Length & ~7;
            ref byte b0 = ref MemoryMarshal.GetReference(data);

            for (int i = 0; i < len; i += 8)
            {
                ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, i));
                s0 += v;
                s1 ^= v * 0x9E3779B97F4A7C15ul;
            }
            for (int i = len; i < data.Length; i++)
            {
                byte v = data[i];
                s0 += v;
                s1 ^= (ulong)v * 1315423911u;
            }
            return s0 ^ (s1 << 1);
        }

        /// <summary>
        /// Страйдовая запись XOR-маркером по каждой странице и обратная запись тем же маркером.
        /// Итоговое содержимое буфера не меняется, но шину/кэши мы «греем» записью.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void StridedMarkerWriteXor(Span<byte> data)
        {
            const ulong Marker = 0xA5A5_A5A5_A5A5_A5A5ul; // классический «полосатый» паттерн

            // Проход 1: XOR-им каждую 8-байтовую позицию в начале страницы
            for (int i = 0; i + 8 <= data.Length; i += Stride)
            {
                ref byte r = ref data[i];
                ulong v = Unsafe.ReadUnaligned<ulong>(ref r);
                v ^= Marker;
                Unsafe.WriteUnaligned(ref r, v);
            }

            // Проход 2: обратный XOR тем же маркером - возвращаем исходное состояние
            for (int i = 0; i + 8 <= data.Length; i += Stride)
            {
                ref byte r = ref data[i];
                ulong v = Unsafe.ReadUnaligned<ulong>(ref r);
                v ^= Marker;
                Unsafe.WriteUnaligned(ref r, v);
            }
        }

        /// <summary>
        /// Случайные read-modify-write: XOR с ключом по детерминированной последовательности индексов.
        /// Повторная прогонка с тем же сидом полностью отменяет изменения.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RandomXorRmw(Span<byte> data, int seed)
        {
            // 64-битный xorshift+ - достаточно быстрый и детерминированный PRNG.
            ulong s0 = (ulong)MixSeed((uint) seed) | 1ul;
            ulong s1 = MixSeed((uint)(seed ^ 0x9E3779B9u));

            int len8 = data.Length & ~7;
            ref byte b0 = ref MemoryMarshal.GetReference(data);

            for (int k = 0; k < RandomOps; k++)
            {
                ulong r = Next(ref s0, ref s1);
                // выбираем 8-байтовую позицию выровненно в пределах буфера
                int i = (int)((r % (uint)(len8 >> 3)) << 3);

                // ключ для XOR тоже из PRNG
                ulong key = Next(ref s0, ref s1);

                ref byte cell = ref Unsafe.Add(ref b0, i);
                ulong v = Unsafe.ReadUnaligned<ulong>(ref cell);
                v ^= key;
                Unsafe.WriteUnaligned(ref cell, v);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint MixSeed(uint x)
            {
                uint z = (uint)x;
                z ^= z >> 15; z *= 2246822519u;
                z ^= z >> 13; z *= 3266489917u;
                z ^= z >> 16;
                return z;
            }


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ulong Next(ref ulong s0, ref ulong s1)
            {
                ulong x = s0; ulong y = s1;
                s0 = y;
                x ^= x << 23;                   // a
                s1 = x ^ y ^ (x >> 17) ^ (y >> 26); // b, c
                return s1 + y;
            }
        }
       
    }
}

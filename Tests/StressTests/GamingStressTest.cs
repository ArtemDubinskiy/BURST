/*
 * Проект:   BURST(Brutal Utilization & Resilience Stress Testing)
 * Автор:    Дубинский Артем {dub}
 */

using System;
using System.Diagnostics;
using System.Numerics;
using BURST.Types.Base;

namespace BURST.Types.StressTests
{
    /// <summary>
    /// Параметры игрового стресс-теста.
    /// </summary>
    public sealed class GamingStressOptions
    {
        public int ObjectCount { get; init; } = 1000;        // количество объектов
        public float Dt { get; init; } = 0.007f;              // ~120 FPS
        public float BoundsMin { get; init; } = 0f;
        public float BoundsMax { get; init; } = 100f;
        public double DurationSeconds { get; init; } = 5.0;   // ограничение времени теста
        public int? Seed { get; init; } = 12345;              // детерминированность; null => Random.Shared
        public bool UseSimd { get; init; } = true;            // векторизация через System.Numerics.Vector<T>
    }

    /// <summary>
    /// Оптимизированный игровой стресс-тест.
    /// Модель памяти: SoA (arrays-of-scalars) для лучшей кэш-локальности и SIMD.
    /// </summary>
    public sealed class GamingStressTest : IStressTest
    {
        private readonly GamingStressOptions _opt;

        // Память под позиции и скорости (SoA).
        private float[] _x, _y, _z;
        private float[] _vx, _vy, _vz;

        // Служебное: валидация и метрики.
        private volatile bool _passed = true;
        private double _checksum;     // накапливаем псевдо-результат, чтобы не выкинуло оптимизацией
        private long _iterations;     // количество «кадров»

        public GamingStressTest() : this(new GamingStressOptions()) { }

        public GamingStressTest(GamingStressOptions options)
        {
            _opt = options ?? new GamingStressOptions();
            AllocateAndInit();
        }

        private void AllocateAndInit()
        {
            int n = _opt.ObjectCount;
            _x = new float[n]; _y = new float[n]; _z = new float[n];
            _vx = new float[n]; _vy = new float[n]; _vz = new float[n];

            Random rnd = _opt.Seed.HasValue ? new Random(_opt.Seed.Value) : Random.Shared;

            float spanX = _opt.BoundsMax - _opt.BoundsMin;
            for (int i = 0; i < n; i++)
            {
                // Позиции в пределах границ:
                _x[i] = _opt.BoundsMin + (float)rnd.NextDouble() * spanX;
                _y[i] = _opt.BoundsMin + (float)rnd.NextDouble() * spanX;
                _z[i] = _opt.BoundsMin + (float)rnd.NextDouble() * spanX;

                // Скорости в диапазоне [-5; 5]:
                _vx[i] = (float)rnd.NextDouble() * 10f - 5f;
                _vy[i] = (float)rnd.NextDouble() * 10f - 5f;
                _vz[i] = (float)rnd.NextDouble() * 10f - 5f;
            }
        }

        /// <summary>
        /// Выполняет нагрузку в течение заданного времени (DurationSeconds).
        /// Внутри цикла избегаем аллокаций, используем MathF и по возможности SIMD.
        /// </summary>
        public void RunTest()
        {
            float dt = _opt.Dt;
            float min = _opt.BoundsMin, max = _opt.BoundsMax;

            // «Игровой» угол для тригонометрии.
            float angle = 0f;

            // Лёгкий «прогрев» JIT: один холостой шаг.
            StepOnce(ref angle, dt, min, max, simd: _opt.UseSimd);

            // Таймбокс теста.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < _opt.DurationSeconds)
            {
                StepOnce(ref angle, dt, min, max, simd: _opt.UseSimd);
                _iterations++;
            }

            // Базовая валидация: конечность сумм/координат + попадание в границы.
            if (!ValidateInternal())
            {
                _passed = false;
            }
        }

        private void StepOnce(ref float angle, float dt, float min, float max, bool simd)
        {
            // Обновление угла и предвычисление тригонометрии (float).
            angle += 0.001f;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            float sumPositions = 0f;

            if (simd && Vector.IsHardwareAccelerated && _x.Length >= Vector<float>.Count)
            {
                StepSimd(dt, min, max, ref sumPositions);
            }
            else
            {
                StepScalar(dt, min, max, ref sumPositions);
            }

            // Имитация применения поворота к вектору (как в вершинном шейдере).
            // Вращение вокруг Y: x' = x*cos + z*sin; z' = -x*sin + z*cos
            float vx = 1f, vy = 1f, vz = 1f;
            float tx = vx * cos + vz * sin;
            float tz = -vx * sin + vz * cos;
            float transformed0 = tx + vy + tz;

            // Доп. вычисления, чтобы загрузить FPU/SIMD.
            float dummy = sumPositions + transformed0;
            // Защита от отрицательного подкоренного - но оставляем нагрузку.
            dummy = MathF.Sqrt(dummy * dummy + MathF.Sin(dummy));

            // Накапливаем контрольную сумму.
            _checksum += dummy;
        }

        private void StepScalar(float dt, float min, float max, ref float sum)
        {
            int n = _x.Length;
            for (int i = 0; i < n; i++)
            {
                _x[i] += _vx[i] * dt;
                _y[i] += _vy[i] * dt;
                _z[i] += _vz[i] * dt;

                // Столкновения c границами + кламп.
                if (_x[i] < min || _x[i] > max) { _vx[i] = -_vx[i]; _x[i] = Clamp(_x[i], min, max); }
                if (_y[i] < min || _y[i] > max) { _vy[i] = -_vy[i]; _y[i] = Clamp(_y[i], min, max); }
                if (_z[i] < min || _z[i] > max) { _vz[i] = -_vz[i]; _z[i] = Clamp(_z[i], min, max); }

                sum += _x[i] + _y[i] + _z[i];
            }
        }

        private void StepSimd(float dt, float min, float max, ref float sum)
        {
            int n = _x.Length;
            int w = Vector<float>.Count;

            var dtv = new Vector<float>(dt);
            var minv = new Vector<float>(min);
            var maxv = new Vector<float>(max);

            int i = 0;
            for (; i <= n - w; i += w)
            {
                var xv = new Vector<float>(_x, i);
                var yv = new Vector<float>(_y, i);
                var zv = new Vector<float>(_z, i);
                var vxv = new Vector<float>(_vx, i);
                var vyv = new Vector<float>(_vy, i);
                var vzv = new Vector<float>(_vz, i);

                // Интегрируем позиции.
                xv += vxv * dtv;
                yv += vyv * dtv;
                zv += vzv * dtv;

                // Отскок: инвертируем скорость по маске (x < min || x > max).
                var flipX = Vector.BitwiseOr(Vector.LessThan(xv, minv), Vector.GreaterThan(xv, maxv));
                vxv = Vector.ConditionalSelect(flipX, -vxv, vxv);

                var flipY = Vector.BitwiseOr(Vector.LessThan(yv, minv), Vector.GreaterThan(yv, maxv));
                vyv = Vector.ConditionalSelect(flipY, -vyv, vyv);

                var flipZ = Vector.BitwiseOr(Vector.LessThan(zv, minv), Vector.GreaterThan(zv, maxv));
                vzv = Vector.ConditionalSelect(flipZ, -vzv, vzv);

                // Кламп позиций.
                xv = Vector.Min(Vector.Max(xv, minv), maxv);
                yv = Vector.Min(Vector.Max(yv, minv), maxv);
                zv = Vector.Min(Vector.Max(zv, minv), maxv);

                // Запись обратно.
                xv.CopyTo(_x, i);
                yv.CopyTo(_y, i);
                zv.CopyTo(_z, i);
                vxv.CopyTo(_vx, i);
                vyv.CopyTo(_vy, i);
                vzv.CopyTo(_vz, i);

                // Сумма позиций (редукция).
                var s = xv + yv + zv;
                float local = 0f;
                for (int k = 0; k < w; k++) local += s[k];
                sum += local;
            }

            // Хвост скаляром.
            for (; i < n; i++)
                StepScalar(dt, min, max, ref sum);
        }

        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);

        public void Validate()
        {
            if (!_passed)
                throw new Exception("Gaming Stress Test validation failed.");
        }

        // Более строгая проверка «после» прогона.
        private bool ValidateInternal()
        {
            if (double.IsNaN(_checksum) || double.IsInfinity(_checksum)) return false;

            float min = _opt.BoundsMin - 1e-3f, max = _opt.BoundsMax + 1e-3f;
            for (int i = 0; i < _x.Length; i++)
            {
                if (!IsFinite(_x[i]) || !IsFinite(_y[i]) || !IsFinite(_z[i])) return false;
                if (_x[i] < min || _x[i] > max) return false;
                if (_y[i] < min || _y[i] > max) return false;
                if (_z[i] < min || _z[i] > max) return false;
            }
            return true;

            static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        }

        // Для отчётности (по желанию можно добавить геттеры).
        public (long Iterations, double Checksum) GetStats() => (_iterations, _checksum);
    }
}

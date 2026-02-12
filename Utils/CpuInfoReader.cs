using LibreHardwareMonitor.Hardware;

namespace BURST.Utils
{
    /// <summary>
    /// Обёртка вокруг LibreHardwareMonitor для выборочного чтения информации о CPU.
    /// <para>Обеспечивает:
    /// <list type="bullet">
    ///   <item><description>Получение человекочитаемого имени CPU.</description></item>
    ///   <item><description>Актуализацию и чтение всех температурных сенсоров CPU.</description></item>
    /// </list>
    /// </para>
    /// <remarks>
    /// Экземпляр <see cref="Computer"/> открывается в конструкторе и закрывается в <see cref="Dispose"/>.
    /// Перед чтением сенсоров вызывается приватный <see cref="UpdateCpuData"/> - это требование LHM для получения свежих значений.
    /// </remarks>
    /// </summary>
    public class CpuInfoReader : IDisposable
    {
        private readonly Computer _computer;  // основной объект LibreHardwareMonitor
        private IHardware? _cpuHardware;       // ссылка на первый найденный узел типа HardwareType.Cpu

        public CpuInfoReader()
        {
            // Включаем только CPU-мониторинг - экономит ресурсы по сравнению с включением всех подсистем
            _computer = new Computer
            {
                IsCpuEnabled = true
            };
            _computer.Open(); // инициализация провайдеров сенсоров

            // Ищем первый HardwareType.Cpu среди доступных железок
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    _cpuHardware = hardware;
                    break;
                }
            }
        }

        public void Dispose()
        {
            _computer?.Close(); // корректное завершение с освобождением ресурсов LHM
        }

        /// <summary>
        /// Возвращает имя CPU, как его определил LibreHardwareMonitor (например, "AMD Ryzen 7 5800X").
        /// При отсутствии информации - "Unknown CPU".
        /// </summary>
        public string GetCpuName()
        {
            return _cpuHardware != null ? _cpuHardware.Name : "Unknown CPU";
        }

        /// <summary>
        /// Запрашивает у LHM обновление кэша сенсоров для узла CPU.
        /// Требуется вызывать <b>перед</b> чтением значений, иначе значения могут быть устаревшими.
        /// </summary>
        private void UpdateCpuData()
        {
            _cpuHardware?.Update();
        }

        /// <summary>
        /// Собирает ВСЕ доступные температурные сенсоры CPU в словарь:
        /// <br/>Ключ - <c>sensor.Name</c> (например, "CPU Core #1", "CPU Package"),
        /// <br/>Значение - <c>sensor.Value</c> (в °C).
        /// </summary>
        /// <returns>Словарь температур. Пустой - если CPU не найден или нет сенсоров.</returns> TODO В планах доработать 
        public Dictionary<string, float> GetTemperatures()
        {
            var temps = new Dictionary<string, float>();

            if (_cpuHardware == null)
                return temps; // Пусто, если узел CPU не обнаружен

            // Обновляем значения перед чтением 
            UpdateCpuData();

            foreach (var sensor in _cpuHardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                {
                    // Например: "CPU Package", "CPU Core #1", "CPU CCD1 (Tdie)" и т.п.
                    temps[sensor.Name] = sensor.Value.Value;
                }
            }
            return temps;
        }
    }
}

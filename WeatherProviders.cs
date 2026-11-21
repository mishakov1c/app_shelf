// Keyless MET Norway locationforecast v2, including city-local forecast dates.
// Pure C# 5 / .NET Framework 4.8 compatible; no insecure TLS bypass or embedded keys.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace AppShelf
{
    [DataContract]
    public sealed class MetResponse
    {
        [DataMember(Name = "properties")] public MetProperties Properties;
    }
    [DataContract]
    public sealed class MetProperties
    {
        [DataMember(Name = "timeseries")] public List<MetStep> Timeseries;
    }
    [DataContract]
    public sealed class MetStep
    {
        [DataMember(Name = "time")] public string Time;
        [DataMember(Name = "data")] public MetData Data;
    }
    [DataContract]
    public sealed class MetData
    {
        [DataMember(Name = "instant")] public MetInstant Instant;
        [DataMember(Name = "next_1_hours")] public MetNext NextHour;
        [DataMember(Name = "next_6_hours")] public MetNext NextSix;
        [DataMember(Name = "next_12_hours")] public MetNext NextTwelve;
    }
    [DataContract]
    public sealed class MetInstant
    {
        [DataMember(Name = "details")] public MetValues Details;
    }
    [DataContract]
    public sealed class MetValues
    {
        [DataMember(Name = "air_temperature")] public double? Temperature;
        [DataMember(Name = "apparent_air_temperature")] public double? FeelsLike;
    }
    [DataContract]
    public sealed class MetNext
    {
        [DataMember(Name = "summary")] public MetSummary Summary;
        [DataMember(Name = "details")] public MetPrecip Details;
    }
    [DataContract]
    public sealed class MetSummary
    {
        [DataMember(Name = "symbol_code")] public string Code;
    }
    [DataContract]
    public sealed class MetPrecip
    {
        [DataMember(Name = "probability_of_precipitation")] public double? Probability;
    }

    // Disposable cache, keyed by the user's city and weather source.
    [DataContract]
    internal sealed class WeatherCacheEntry
    {
        [DataMember(Name = "city")] public string City;
        [DataMember(Name = "provider")] public string Provider;
        [DataMember(Name = "savedUtcTicks")] public long SavedUtcTicks;
        [DataMember(Name = "forecast")] public WeatherSnapshot Forecast;
    }

    [DataContract]
    internal sealed class WeatherCacheDocument
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "entries")] public List<WeatherCacheEntry> Entries;
    }

    public static class WeatherDiskCache
    {
        public static readonly string FilePath = Path.Combine(ConfigStore.DirectoryPath, "weather-cache.json");
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
        private const int MaxCacheBytes = 512 * 1024;
        private const int MaxEntries = 16;
        private static readonly object Sync = new object();

        public static bool IsFresh(DateTime savedUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - savedUtc;
            return age >= TimeSpan.FromMinutes(-1) && age < Lifetime;
        }

        private static bool ValidForecast(WeatherSnapshot forecast)
        {
            if (forecast == null || forecast.Days == null || forecast.Days.Count > 7 ||
                String.IsNullOrWhiteSpace(forecast.City) ||
                Double.IsNaN(forecast.Temperature) || Double.IsInfinity(forecast.Temperature))
                return false;
            foreach (WeatherDay day in forecast.Days)
                if (day == null || day.Date == DateTime.MinValue) return false;
            return true;
        }

        private static bool TryTimestamp(WeatherCacheEntry entry, DateTime nowUtc, out DateTime savedUtc)
        {
            savedUtc = DateTime.MinValue;
            if (entry == null || entry.SavedUtcTicks < DateTime.MinValue.Ticks ||
                entry.SavedUtcTicks > DateTime.MaxValue.Ticks || !ValidForecast(entry.Forecast))
                return false;
            savedUtc = new DateTime(entry.SavedUtcTicks, DateTimeKind.Utc);
            return IsFresh(savedUtc, nowUtc);
        }

        private static WeatherCacheDocument Read()
        {
            if (!File.Exists(FilePath)) return null;
            try
            {
                using (FileStream stream = File.OpenRead(FilePath))
                {
                    if (stream.Length == 0 || stream.Length > MaxCacheBytes) return null;
                    WeatherCacheDocument data = (WeatherCacheDocument)
                        new DataContractJsonSerializer(typeof(WeatherCacheDocument)).ReadObject(stream);
                    return data != null && data.Version == 2 && data.Entries != null &&
                        data.Entries.Count <= MaxEntries ? data : null;
                }
            }
            catch (Exception ex)
            {
                // The cache is disposable. A damaged file cannot prevent startup or future saves.
                ConfigStore.Log(new InvalidDataException("Не удалось прочитать кеш погоды.", ex));
                return null;
            }
        }

        public static bool TryLoad(string city, string provider, DateTime nowUtc,
            out WeatherSnapshot forecast, out DateTime savedUtc)
        {
            forecast = null;
            savedUtc = DateTime.MinValue;
            if (String.IsNullOrWhiteSpace(city) || String.IsNullOrWhiteSpace(provider)) return false;
            lock (Sync)
            {
                WeatherCacheDocument data = Read();
                if (data == null) return false;
                foreach (WeatherCacheEntry entry in data.Entries)
                {
                    DateTime timestamp;
                    if (entry != null &&
                        String.Equals(entry.City, city, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(entry.Provider, provider, StringComparison.Ordinal) &&
                        TryTimestamp(entry, nowUtc, out timestamp))
                    {
                        forecast = entry.Forecast;
                        savedUtc = timestamp;
                        return true;
                    }
                }
            }
            return false;
        }

        public static void Save(string city, string provider, WeatherSnapshot forecast, DateTime savedUtc)
        {
            if (String.IsNullOrWhiteSpace(city) || String.IsNullOrWhiteSpace(provider) ||
                !ValidForecast(forecast)) return;
            string temporary = FilePath + ".tmp";
            lock (Sync)
            {
                try
                {
                    DateTime nowUtc = DateTime.UtcNow;
                    WeatherCacheDocument current = Read();
                    List<WeatherCacheEntry> entries = new List<WeatherCacheEntry>();
                    if (current != null)
                        foreach (WeatherCacheEntry old in current.Entries)
                        {
                            DateTime oldTimestamp;
                            if (!TryTimestamp(old, nowUtc, out oldTimestamp)) continue;
                            if (String.Equals(old.City, city, StringComparison.OrdinalIgnoreCase) &&
                                String.Equals(old.Provider, provider, StringComparison.Ordinal)) continue;
                            entries.Add(old);
                        }
                    entries.Add(new WeatherCacheEntry
                    {
                        City = city, Provider = provider,
                        SavedUtcTicks = savedUtc.ToUniversalTime().Ticks, Forecast = forecast
                    });
                    entries.Sort(delegate(WeatherCacheEntry x, WeatherCacheEntry y)
                    {
                        return y.SavedUtcTicks.CompareTo(x.SavedUtcTicks);
                    });
                    if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                    Directory.CreateDirectory(ConfigStore.DirectoryPath);
                    WeatherCacheDocument data = new WeatherCacheDocument { Version = 2, Entries = entries };
                    using (FileStream stream = new FileStream(temporary, FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(WeatherCacheDocument)).WriteObject(stream, data);
                        if (stream.Length > MaxCacheBytes)
                            throw new InvalidDataException("Кеш погоды слишком большой.");
                        stream.Flush(true);
                    }
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                    else File.Move(temporary, FilePath);
                }
                catch (Exception ex)
                {
                    // A storage error must not turn successfully loaded weather into a UI failure.
                    ConfigStore.Log(new IOException("Не удалось сохранить кеш погоды.", ex));
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch { }
                }
            }
        }
    }

    public static partial class WeatherClient
    {
        // Windows supplies the CLDR mapping; do not guess offsets or keep a city/zone table.
        [DllImport("icu.dll", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ucal_getWindowsTimeZoneID(string id, int length,
            StringBuilder result, int capacity, ref int status);

        private static TimeZoneInfo ResolveWeatherTimeZone(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new InvalidDataException("MET Norway: часовой пояс города не указан.");
            try
            {
                // Modern .NET also accepts IANA IDs directly; Framework needs Windows IDs.
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                StringBuilder windowsId = new StringBuilder(128);
                int status = 0;
                int length = ucal_getWindowsTimeZoneID(id, id.Length, windowsId, windowsId.Capacity, ref status);
                if (status > 0 || length <= 0 || length >= windowsId.Capacity)
                    throw new TimeZoneNotFoundException(id);
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId.ToString());
            }
            catch (Exception ex)
            {
                if (!(ex is TimeZoneNotFoundException) && !(ex is InvalidTimeZoneException) &&
                    !(ex is DllNotFoundException) && !(ex is EntryPointNotFoundException)) throw;
                throw new InvalidDataException("MET Norway: не удалось определить часовой пояс города '" + id +
                    "'. Нужны актуальные часовые пояса и системная ICU Windows (Windows 10 1903 или новее).", ex);
            }
        }

        private static string DescribeMet(string code)
        {
            if (String.IsNullOrEmpty(code)) return "н/д";
            code = code.ToLowerInvariant();
            if (code.Contains("thunder")) return "гроза";
            if (code.Contains("snow")) return "снег";
            if (code.Contains("sleet")) return "мокрый снег";
            if (code.Contains("rain")) return "дождь";
            if (code.Contains("fog")) return "туман";
            if (code.Contains("clearsky")) return "ясно";
            if (code.Contains("partlycloudy")) return "переменная облачность";
            if (code.Contains("cloud")) return "облачно";
            return "переменная погода";
        }

        public static async Task<WeatherSnapshot> GetMetNorwayForecast(string city, string lat, string lon, bool kemerovo, string timeZoneId = null)
        {
            // Kemerovo's existing built-in location belongs to this Windows zone (UTC+7).
            TimeZoneInfo zone = ResolveWeatherTimeZone(kemerovo ? "North Asia Standard Time" : timeZoneId);
            // The provider requests 4 decimal places for caching and refresh no more often than every 3 h outside Nordics.
            string lat4 = Math.Round(Double.Parse(lat, CultureInfo.InvariantCulture), 4)
                .ToString("0.####", CultureInfo.InvariantCulture);
            string lon4 = Math.Round(Double.Parse(lon, CultureInfo.InvariantCulture), 4)
                .ToString("0.####", CultureInfo.InvariantCulture);
            MetResponse data = await ReadJson<MetResponse>(
                "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=" + lat4 + "&lon=" + lon4,
                "MET Norway");
            if (data == null || data.Properties == null || data.Properties.Timeseries == null ||
                data.Properties.Timeseries.Count == 0)
                throw new InvalidDataException("MET Norway: пустой прогноз.");
            Dictionary<DateTime, WeatherDay> days = new Dictionary<DateTime, WeatherDay>();
            MetValues current = null;
            string currentCode = "";
            DateTime today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
            foreach (MetStep item in data.Properties.Timeseries)
            {
                DateTimeOffset time;
                if (item == null || !DateTimeOffset.TryParse(item.Time, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out time))
                    throw new InvalidDataException("MET Norway: отсутствует корректное время точки прогноза.");
                DateTimeOffset localTime = TimeZoneInfo.ConvertTime(time, zone);
                DateTime localDate = localTime.Date;
                if (localDate < today || localDate >= today.AddDays(7)) continue;
                if (item.Data == null || item.Data.Instant == null || item.Data.Instant.Details == null)
                    throw new InvalidDataException("MET Norway: отсутствуют обязательные данные точки прогноза.");
                MetValues details = item.Data.Instant.Details;
                MetNext next = item.Data.NextHour ?? item.Data.NextSix ?? item.Data.NextTwelve;
                string symbol = next != null && next.Summary != null ? next.Summary.Code : null;
                if (current == null)
                {
                    if (!details.Temperature.HasValue) throw new InvalidDataException("MET Norway: отсутствует текущая температура.");
                    current = details; currentCode = symbol;
                }
                WeatherDay day;
                if (!days.TryGetValue(localDate, out day))
                {
                    day = new WeatherDay { Date = localDate, Condition = DescribeMet(symbol),
                        Min = details.Temperature, Max = details.Temperature,
                        ApparentMin = details.FeelsLike, ApparentMax = details.FeelsLike };
                    days.Add(localDate, day);
                }
                // A missing sample must not turn a partial range into a complete daily range.
                day.Min = day.Min.HasValue && details.Temperature.HasValue ? (double?)Math.Min(day.Min.Value, details.Temperature.Value) : null;
                day.Max = day.Max.HasValue && details.Temperature.HasValue ? (double?)Math.Max(day.Max.Value, details.Temperature.Value) : null;
                day.ApparentMin = day.ApparentMin.HasValue && details.FeelsLike.HasValue ? (double?)Math.Min(day.ApparentMin.Value, details.FeelsLike.Value) : null;
                day.ApparentMax = day.ApparentMax.HasValue && details.FeelsLike.HasValue ? (double?)Math.Max(day.ApparentMax.Value, details.FeelsLike.Value) : null;
                if (localTime.Hour >= 11 && localTime.Hour <= 14 && !String.IsNullOrEmpty(symbol))
                    day.Condition = DescribeMet(symbol);
                if (next != null && next.Details != null && next.Details.Probability.HasValue)
                {
                    int p = (int)Math.Round(next.Details.Probability.Value);
                    day.PrecipitationProbability = !day.PrecipitationProbability.HasValue ? p :
                        Math.Max(day.PrecipitationProbability.Value, p);
                }
            }
            if (current == null || days.Count == 0) throw new InvalidDataException("MET Norway: нет прогноза на неделю.");
            WeatherSnapshot snapshot = new WeatherSnapshot
            {
                City = city, Source = "MET Norway",
                Temperature = current.Temperature.Value, ApparentTemperature = current.FeelsLike,
                DailyRangeNote = "Диапазоны — по точкам прогноза за местный день города, не измеренные суточные экстремумы.",
                Condition = DescribeMet(currentCode), UpdatedAt = DateTime.Now
            };
            for (int i = 0; i < 7; i++)
            {
                WeatherDay day;
                if (days.TryGetValue(today.AddDays(i), out day)) snapshot.Days.Add(day);
            }
            return snapshot;
        }

    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.Plugins.Native
{
    public static class CryptoNative
    {
        private sealed class SymbolInfo
        {
            public string Base { get; init; } = "BTC";
            public string Quote { get; init; } = "USDT";
            public string ApiSymbol => Base + Quote;
            public string OkxSymbol => Base + "-" + Quote;
            public string GateSymbol => Base + "_" + Quote;
        }

        private sealed class QuoteCache
        {
            public string Json { get; init; } = "";
            public DateTime Time { get; init; }
            public bool IsFallback { get; init; }
        }

        private static readonly object _clientLock = new object();
        private static readonly ConcurrentDictionary<string, QuoteCache> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflightRequests = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan _directCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _fallbackCacheTtl = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan _staleCacheTtl = TimeSpan.FromMinutes(30);
        private static DateTime _fallbackBlockedUntil = DateTime.MinValue;
        private static HttpClient _client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 8,
                UseProxy = true,
                Proxy = System.Net.WebRequest.GetSystemWebProxy()
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
            return client;
        }

        public static void ResetClient()
        {
            HttpClient old;
            lock (_clientLock)
            {
                old = _client;
                _client = CreateClient();
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try { old.Dispose(); } catch { }
            });
        }

        private static HttpClient GetClient()
        {
            lock (_clientLock)
            {
                return _client;
            }
        }

        public static async Task<string> FetchAsync(string symbol, string fallbackUrl)
        {
            var info = NormalizeSymbol(symbol);
            string cacheKey = info.ApiSymbol;

            if (TryGetFreshCache(cacheKey, out var cached))
            {
                return cached;
            }

            var lazyTask = _inflightRequests.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<string>>(
                    () => FetchFreshAsync(info, fallbackUrl, cacheKey),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await lazyTask.Value;
            }
            finally
            {
                // 只移除当前 Lazy，避免误删同 key 下刚创建的新请求。
                _inflightRequests.TryRemove(
                    new KeyValuePair<string, Lazy<Task<string>>>(cacheKey, lazyTask));
            }
        }

        private static async Task<string> FetchFreshAsync(SymbolInfo info, string fallbackUrl, string cacheKey)
        {
            foreach (var source in GetOfficialSources(info))
            {
                try
                {
                    string json = await GetStringAsync(source.url, TimeSpan.FromSeconds(3));
                    string normalized = source.parser(json, info);
                    SaveCache(cacheKey, normalized, false);
                    return normalized;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CryptoNative] 官方源 {source.name} 失败: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                if (DateTime.Now < _fallbackBlockedUntil)
                {
                    if (TryGetStaleCache(cacheKey, out var stale))
                        return stale;

                    throw new Exception("Crypto Native Failed: fallback is cooling down.");
                }

                try
                {
                    string targetUrl = BuildFallbackUrl(fallbackUrl, info.ApiSymbol);
                    string fallbackJson = await GetStringAsync(targetUrl, TimeSpan.FromSeconds(8));
                    SaveCache(cacheKey, fallbackJson, true);
                    return fallbackJson;
                }
                catch (Exception ex)
                {
                    BlockFallback(ex);
                    if (TryGetStaleCache(cacheKey, out var stale))
                        return stale;

                    throw new Exception($"Crypto Native Failed (Official & Fallback): {ex.Message}");
                }
            }

            if (TryGetStaleCache(cacheKey, out var last))
                return last;

            throw new Exception("Crypto Native Failed: official APIs failed and no fallback provided.");
        }

        private static SymbolInfo NormalizeSymbol(string symbol)
        {
            string raw = (symbol ?? "BTC").Trim().ToUpperInvariant().Replace("/", "-").Replace("_", "-");
            string[] parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return new SymbolInfo { Base = parts[0], Quote = NormalizeQuote(parts[1]) };
            }

            string compact = raw.Replace("-", "");
            if (compact.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            {
                return new SymbolInfo { Base = compact[..^4], Quote = "USDT" };
            }
            if (compact.EndsWith("USD", StringComparison.OrdinalIgnoreCase))
            {
                return new SymbolInfo { Base = compact[..^3], Quote = "USDT" };
            }

            return new SymbolInfo { Base = compact.Length == 0 ? "BTC" : compact, Quote = "USDT" };
        }

        private static string NormalizeQuote(string quote)
        {
            return quote.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "USDT" : quote.ToUpperInvariant();
        }

        private static IEnumerable<(string name, string url, Func<string, SymbolInfo, string> parser)> GetOfficialSources(SymbolInfo info)
        {
            // 优先保持原有 Bybit 数据源；GateIO 作为国内直连兜底，减少中转消耗。
            yield return ("Bybit", $"https://api.bybit.com/v5/market/tickers?category=spot&symbol={info.ApiSymbol}", ParseBybitResponse);
            yield return ("GateIO", $"https://api.gateio.ws/api/v4/spot/tickers?currency_pair={info.GateSymbol}", ParseGateResponse);
            yield return ("Binance", $"https://api.binance.com/api/v3/ticker/24hr?symbol={info.ApiSymbol}", ParseBinanceResponse);
            yield return ("OKX", $"https://www.okx.com/api/v5/market/ticker?instId={info.OkxSymbol}", ParseOkxResponse);
        }

        private static async Task<string> GetStringAsync(string url, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var response = await GetClient().GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
            }
            return await response.Content.ReadAsStringAsync(cts.Token);
        }

        private static string BuildFallbackUrl(string fallbackUrl, string symbol)
        {
            string targetUrl = fallbackUrl.Replace("{{symbol}}", Uri.EscapeDataString(symbol));
            if (!targetUrl.Contains("symbol=", StringComparison.OrdinalIgnoreCase) &&
                !targetUrl.Contains(symbol, StringComparison.OrdinalIgnoreCase))
            {
                targetUrl += (targetUrl.Contains("?") ? "&" : "?") + $"symbol={Uri.EscapeDataString(symbol)}";
            }
            return targetUrl;
        }

        private static bool TryGetFreshCache(string key, out string json)
        {
            json = "";
            if (!_cache.TryGetValue(key, out var entry)) return false;

            var ttl = entry.IsFallback ? _fallbackCacheTtl : _directCacheTtl;
            if (DateTime.Now - entry.Time > ttl) return false;

            json = entry.Json;
            return true;
        }

        private static bool TryGetStaleCache(string key, out string json)
        {
            json = "";
            if (!_cache.TryGetValue(key, out var entry)) return false;
            if (DateTime.Now - entry.Time > _staleCacheTtl) return false;

            json = entry.Json;
            return true;
        }

        private static void SaveCache(string key, string json, bool isFallback)
        {
            _cache[key] = new QuoteCache { Json = json, Time = DateTime.Now, IsFallback = isFallback };
        }

        private static void BlockFallback(Exception ex)
        {
            TimeSpan cooldown = IsQuotaError(ex) ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);
            _fallbackBlockedUntil = DateTime.Now.Add(cooldown);
            System.Diagnostics.Debug.WriteLine($"[CryptoNative] 中转源冷却 {cooldown.TotalMinutes:0} 分钟: {ex.Message}");
        }

        private static bool IsQuotaError(Exception ex)
        {
            if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            {
                return httpEx.StatusCode.Value == HttpStatusCode.TooManyRequests ||
                       httpEx.StatusCode.Value == HttpStatusCode.Forbidden ||
                       httpEx.StatusCode.Value == HttpStatusCode.ServiceUnavailable;
            }
            return false;
        }

        private static string ParseBybitResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            
            if (root.GetProperty("retCode").GetInt32() != 0)
            {
                throw new Exception($"Bybit Error: {root.GetProperty("retMsg").GetString()}");
            }

            var list = root.GetProperty("result").GetProperty("list");
            if (list.GetArrayLength() == 0)
            {
                throw new Exception("Symbol Not Found");
            }

            var data = list[0];

            double pcnt = 0;
            if (double.TryParse(data.GetProperty("price24hPcnt").GetString(), out double p))
            {
                pcnt = Math.Round(p * 100, 2);
            }

            var responseData = new
            {
                name = data.GetProperty("symbol").GetString(),
                price = data.GetProperty("lastPrice").GetString(),
                change_percent = pcnt,
                high = data.GetProperty("highPrice24h").GetString(),
                low = data.GetProperty("lowPrice24h").GetString(),
                vol = data.GetProperty("turnover24h").GetString(),
                source = "Bybit"
            };

            return JsonSerializer.Serialize(responseData);
        }

        private static string ParseBinanceResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var data = doc.RootElement;

            double pcnt = 0;
            double.TryParse(data.GetProperty("priceChangePercent").GetString(), out pcnt);

            var responseData = new
            {
                name = data.GetProperty("symbol").GetString(),
                price = data.GetProperty("lastPrice").GetString(),
                change_percent = Math.Round(pcnt, 2),
                high = data.GetProperty("highPrice").GetString(),
                low = data.GetProperty("lowPrice").GetString(),
                vol = data.GetProperty("quoteVolume").GetString(),
                source = "Binance"
            };

            return JsonSerializer.Serialize(responseData);
        }

        private static string ParseGateResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var list = doc.RootElement;
            if (list.GetArrayLength() == 0)
            {
                throw new Exception("Symbol Not Found");
            }

            var data = list[0];
            double pcnt = 0;
            double.TryParse(data.GetProperty("change_percentage").GetString(), out pcnt);

            var responseData = new
            {
                name = info.ApiSymbol,
                price = data.GetProperty("last").GetString(),
                change_percent = Math.Round(pcnt, 2),
                high = data.GetProperty("high_24h").GetString(),
                low = data.GetProperty("low_24h").GetString(),
                vol = data.GetProperty("quote_volume").GetString(),
                source = "GateIO"
            };

            return JsonSerializer.Serialize(responseData);
        }

        private static string ParseOkxResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetString() != "0")
            {
                throw new Exception($"OKX Error: {root.GetProperty("msg").GetString()}");
            }

            var list = root.GetProperty("data");
            if (list.GetArrayLength() == 0)
            {
                throw new Exception("Symbol Not Found");
            }

            var data = list[0];
            double open = 0;
            double last = 0;
            double.TryParse(data.GetProperty("open24h").GetString(), out open);
            double.TryParse(data.GetProperty("last").GetString(), out last);
            double pcnt = open > 0 ? Math.Round((last - open) / open * 100, 2) : 0;

            var responseData = new
            {
                name = info.ApiSymbol,
                price = data.GetProperty("last").GetString(),
                change_percent = pcnt,
                high = data.GetProperty("high24h").GetString(),
                low = data.GetProperty("low24h").GetString(),
                vol = data.GetProperty("volCcy24h").GetString(),
                source = "OKX"
            };

            return JsonSerializer.Serialize(responseData);
        }
    }
}

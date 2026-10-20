using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor
{
    public static class NetworkSpeedTester
    {
        // 增加 HttpClient 实例的连接池，避免频繁创建连接
        private static readonly HttpClient http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), // 连接重用
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 32 // 增加到服务器的最大连接数
        })
        {
            Timeout = TimeSpan.FromSeconds(25) // 增加全局超时时间，为下载预留更多时间
        };

        private static readonly Random _rng = new Random();
        private static string Rand() { lock (_rng) return _rng.NextDouble().ToString("0.0000000000"); }

        // 优化的下载源列表 - 按地区分组 (已包含所有优质节点)
        private static readonly string[] DownloadSources =
        {
            // --- 第一梯队：中国大陆国内镜像 (物理距离近，带宽易跑满) ---
            "https://mirrors.huaweicloud.com/repository/ubuntu/pool/main/l/linux/linux-source-5.4.0_5.4.0-89.100_all.deb?r=", // 华为云 CDN (国内极速)
            "https://mirrors.aliyun.com/debian/pool/main/g/gcc-10/gcc-10.2.1-6_amd64.deb?r=", // 阿里云 CDN (国内极速)
            "https://mirrors.tuna.tsinghua.edu.cn/ubuntu/pool/main/l/linux/linux-source-5.4.0_5.4.0-89.100_all.deb?r=",   // 清华大学 TUNA (教育网/三大运营商优化)

            // --- 第二梯队：全球CDN / 优质直连线路 (CN2 GIA/低延迟) ---
            "https://speed.cloudflare.com/__down?during=download&bytes=50000000&r=", // CloudFlare (全球CDN，虽有波动但覆盖最广)
            "https://lg.bandwagonhost.com/100MB.test?r=",       // 搬瓦工 洛杉矶 (CN2 GIA 线路，晚高峰不掉速)
            "http://la-cn2-gia.lg.dmit.io/100MB.test?r=",        // DMIT 洛杉矶 (CN2 GIA 高端线路)
            "http://hk-main.lg.misaka.io/100MB.test?r=",         // Misaka 香港 (大陆直连优化)

            // --- 第三梯队：亚洲地区常规节点 (原有列表 + 补充) ---
            "https://speedtest.hgc.jp/downloading?n=",                                                    // 日本 HGC (响应快)
            "https://seednet-ty1.seed.net.tw.prod.hosts.ooklaserver.net:8080/download?size=50000000&r=", // 台湾 Seednet (亚洲大带宽)
            "https://hnd-jp-ping.vultr.com/vultr.com.100MB.bin?r=",                                      // 日本 Vultr
            "https://sgp-ping.vultr.com/vultr.com.100MB.bin?r=",                                         // 新加坡 Vultr

            // --- 第四梯队：欧美备用节点 (延迟较高) ---
            "https://la.speedtest.clouvider.net/backend/garbage.php?cors=true&size=25000000&r=",  // 洛杉矶 Clouvider
            "https://ams.speedtest.clouvider.net/backend/garbage.php?cors=true&size=25000000&r=", // 阿姆斯特丹 Clouvider
            "https://atl.speedtest.clouvider.net/backend/garbage.php?cors=true&size=25000000&r=", // 亚特兰大 Clouvider
        };

        // 优化的上传源列表
        private static readonly string[] UploadSources =
        {
            // --- 第一梯队：全球 Anycast (智能路由，自动选最近) ---
            "https://speed.cloudflare.com/__up?measId=",   // CloudFlare (全球CDN上传，最稳健)

            // --- 第二梯队：亚洲优质节点 (物理距离近，上传损耗小) ---
            "https://tyo.speedtest.clouvider.net/backend/empty.php?cors=true&r=",        // 日本东京 Clouvider (对华路由优化，推荐)
            "https://speedtest.hgc.jp/upload?n=",                                        // 日本 HGC (原有，上传专用)
            "http://seednet-ty1.seed.net.tw.prod.hosts.ooklaserver.net:8080/upload?r=",  // 台湾 Seednet (大带宽节点)

            // --- 第三梯队：北美 (测试跨境带宽) ---
            "https://la.speedtest.clouvider.net/backend/empty.php?cors=true&r=",         // 洛杉矶 Clouvider (电信/联通友好)
            "http://los-angeles-ca-speedtest.reliablesite.net/empty.php?r=",             // 洛杉矶 ReliableSite (备用)

            // --- 第四梯队：欧洲 (原有备选) ---
            "https://ams.speedtest.clouvider.net/backend/empty.php?cors=true&r=",        // 阿姆斯特丹 Clouvider
            "https://atl.speedtest.clouvider.net/backend/empty.php?cors=true&r=",        // 亚特兰大 Clouvider
        };

        // 共享变量，用于存储动态的最佳 URL
        private static string currentBestDownloadUrl = DownloadSources[0];
        private static string currentBestUploadUrl = UploadSources[0];

        // 日志记录
        private static void Log(string message)
        {
            Debug.WriteLine($"[NetworkSpeedTester] {DateTime.Now:HH:mm:ss.fff} - {message}");
        }

        // ======================================================
        // 1. 优化的竞速逻辑
        // ======================================================
        private static async Task<string> PickFastestAsync(string[] urls, int raceTimeMs, bool isDownload = true)
        {
            var tasks = new List<Task<(string url, long bytes, bool success)>>();

            foreach (var url in urls)
            {
                // 仅对前七个（国内和优质CN2）进行更快的竞速，其余作为备选 (这里只跳过奇数索引，减少并发压力)
                if (Array.IndexOf(urls, url) > 6 && Array.IndexOf(urls, url) % 2 == 0) continue; 
                
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] buf = new byte[256 * 1024]; // 256KB 缓冲区
                        long bytes = 0;
                        int attempts = 0;
                        const int maxAttempts = 2; // 减少尝试次数

                        while (attempts < maxAttempts)
                        {
                            try
                            {
                                // 每次尝试都创建新的 cts
                                using var cts = new CancellationTokenSource(raceTimeMs + 2000); // 额外2秒容错
                                using var req = new HttpRequestMessage(HttpMethod.Get, url + Rand());
                                
                                // 设置请求头，模仿浏览器行为，可能提高国内节点成功率
                                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

                                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                                
                                if (!resp.IsSuccessStatusCode)
                                {
                                    Log($"{url} - HTTP {resp.StatusCode}");
                                    attempts++;
                                    continue;
                                }

                                using var stream = await resp.Content.ReadAsStreamAsync();
                                var sw = Stopwatch.StartNew();
                                
                                // 持续读取直到竞速时间结束
                                while (sw.ElapsedMilliseconds < raceTimeMs)
                                {
                                    int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                                    if (n <= 0) break;
                                    bytes += n;
                                }

                                Log($"{url} - Success: {bytes / 1024.0 / 1024.0:F2} MB in {sw.ElapsedMilliseconds}ms");
                                return (url, bytes, true);
                            }
                            catch (Exception ex)
                            {
                                Log($"{url} - Attempt {attempts + 1} failed: {ex.Message}");
                                attempts++;
                                if (attempts >= maxAttempts) break;
                                
                                // 修复 CS0103 错误：cts 在 catch 块中超出了作用域
                                await Task.Delay(50); // 只进行 50ms 延迟，不使用 cts.Token
                            }
                        }

                        return (url, 0, false);
                    }
                    catch (Exception ex)
                    {
                        Log($"{url} - Critical error: {ex.Message}");
                        return (url, 0, false);
                    }
                }));
            }

            var results = await Task.WhenAll(tasks);
            var successfulResults = results.Where(r => r.success).ToArray();

            if (successfulResults.Length == 0)
            {
                Log("All download sources failed, using fallback");
                return urls[0]; // 返回第一个作为备选
            }

            Array.Sort(successfulResults, (a, b) => b.bytes.CompareTo(a.bytes));
            var best = successfulResults[0];
            Log($"Best { (isDownload ? "download" : "upload") } source: {best.url} - {best.bytes / 1024.0 / 1024.0:F2} MB");
            
            return best.url;
        }

        // ======================================================
        // 2. 优化的下载测速 (提高并发，增加竞速频率)
        // ======================================================
        public static async Task<double> TestDownloadAsync(
            int durationSec = 15,
            int threads = 16, // 优化点1：增加线程数以跑满带宽
            Action<double>? progress = null)
        {
            Log("Starting download test...");
            Interlocked.Exchange(ref currentBestDownloadUrl, DownloadSources[0]);

            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            List<Task> worker = new();
            var cts = new CancellationTokenSource();

            // 优化的管理线程 - 提高竞速频率
            var managerTask = Task.Run(async () =>
            {
                while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // 优化点2：缩短竞速评估间隔，更快切换到最佳节点
                        string best = await PickFastestAsync(DownloadSources, raceTimeMs: 2000, isDownload: true);
                        Interlocked.Exchange(ref currentBestDownloadUrl, best);
                        
                        // 每 1.5 秒重新评估一次
                        await Task.Delay(1500, cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Manager task error: {ex.Message}");
                    }
                }
            });

            // 工作线程
            for (int i = 0; i < threads; i++)
            {
                worker.Add(Task.Run(async () =>
                {
                    // ★★★ 优化：512KB缓冲区（平衡性能与内存）★★★
                    byte[] buf = new byte[512 * 1024]; 

                    while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            string dynamicUrl = currentBestDownloadUrl;
                            // Log($"Worker using: {dynamicUrl}"); // 避免日志过多

                            using var req = new HttpRequestMessage(HttpMethod.Get, dynamicUrl + Rand());
                            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

                            // 设置单个请求超时 (20秒)
                            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, new CancellationTokenSource(20000).Token);
                            
                            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, workerCts.Token);
                            resp.EnsureSuccessStatusCode();

                            using var stream = await resp.Content.ReadAsStreamAsync();

                            while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                            {
                                int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                                if (n <= 0) break;

                                long now = Interlocked.Add(ref totalBytes, n);
                                double speedMbps = (now * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                                progress?.Invoke(speedMbps);
                            }
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log($"Worker error: {ex.Message}");
                            await Task.Delay(500, cts.Token); // 错误后短暂延迟
                        }
                    }
                }));
            }

            try
            {
                // 等待测试时间结束
                await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
            }
            catch (TaskCanceledException) { }

            // 取消所有任务
            cts.Cancel();
            
            // 优化点3：增加 Task.WhenAll 的等待时限，避免卡死在收尾阶段
            try
            {
                // WaitAsync 需要 .NET 6 或更高版本，如果版本低于此，请替换为 Task.WhenAll + CancellationTokenSource
                await Task.WhenAll(worker.Concat(new[] { managerTask }))
                          .WaitAsync(TimeSpan.FromSeconds(5)); // 最多等待 5 秒清理
            }
            catch (TimeoutException)
            {
                 Log("Download cleanup timed out, exiting gracefully.");
            }
            catch (Exception ex)
            {
                Log($"Final download await error: {ex.Message}");
            }

            double finalSpeed = Math.Round((totalBytes * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1), 1);
            Log($"Download test completed: {finalSpeed} Mbps, Total: {totalBytes / 1024.0 / 1024.0:F2} MB");

            // ★★★ 优化：强制清理内存 ★★★
            worker.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            return finalSpeed;
        }

        // ======================================================
        // 3. 优化的上传测速 (增加请求超时，解决卡顿)
        // ======================================================
        public static async Task<double> TestUploadAsync(
            int durationSec = 5,
            int threads = 8, // 优化点1：增加上传线程数
            Action<double>? progress = null)
        {
            Log("Starting upload test...");
            Interlocked.Exchange(ref currentBestUploadUrl, UploadSources[0]);

            // ★★★ 优化：上传负载 512KB ★★★
            byte[] payload = new byte[512 * 1024]; 
            _rng.NextBytes(payload);

            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            List<Task> worker = new();
            var cts = new CancellationTokenSource();

            // 管理线程 (保持竞速评估频率优化)
            var managerTask = Task.Run(async () =>
            {
                while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        string best = await PickFastestAsync(UploadSources, raceTimeMs: 2000, isDownload: false);
                        Interlocked.Exchange(ref currentBestUploadUrl, best);
                        // 每 1.5 秒重新评估一次
                        await Task.Delay(1500, cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Upload manager error: {ex.Message}");
                    }
                }
            });

            for (int i = 0; i < threads; i++)
            {
                worker.Add(Task.Run(async () =>
                {
                    var contentType = new MediaTypeHeaderValue("application/octet-stream");

                    while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                    {
                        // 优化点2：给每个 POST 请求一个严格的超时 (5秒)，防止卡顿
                        using var uploadTimeoutCts = new CancellationTokenSource(5000); 
                        // 联合 Token：外部取消信号 或 内部超时信号 都会触发
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, uploadTimeoutCts.Token);
                        string url = currentBestUploadUrl + Rand();
                        try
                        {
                            var content = new ByteArrayContent(payload);
                            content.Headers.ContentType = contentType;
                            
                            // 使用联合 Token
                            using var resp = await http.PostAsync(url, content, linkedCts.Token);
                            resp.EnsureSuccessStatusCode();

                            long now = Interlocked.Add(ref totalBytes, payload.Length);
                            double speedMbps = (now * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                            progress?.Invoke(speedMbps);
                        }
                        catch (TaskCanceledException ex) when (uploadTimeoutCts.IsCancellationRequested)
                        {
                            // 如果是内部超时导致的取消，记录并继续
                            Log($"Upload request timed out for worker: {url}");
                            await Task.Delay(500, cts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            break; // 外部取消信号，退出循环
                        }
                        catch (Exception ex)
                        {
                            Log($"Upload worker error: {ex.Message}");
                            await Task.Delay(500, cts.Token);
                        }
                    }
                }));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
            }
            catch (TaskCanceledException) { }

            cts.Cancel();
            
            // 优化点3：增加 Task.WhenAll 的等待时限
            try
            {
                await Task.WhenAll(worker.Concat(new[] { managerTask }))
                          .WaitAsync(TimeSpan.FromSeconds(5)); // 最多等待 5 秒清理
            }
            catch (TimeoutException)
            {
                Log("Upload cleanup timed out, exiting gracefully.");
            }
            catch (Exception ex)
            {
                Log($"Upload final await error: {ex.Message}");
            }

            double finalSpeed = Math.Round((totalBytes * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1), 1);
            Log($"Upload test completed: {finalSpeed} Mbps, Total: {totalBytes / 1024.0 / 1024.0:F2} MB");

            // ★★★ 优化：强制清理内存 ★★★
            worker.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            return finalSpeed;
        }
    }
}

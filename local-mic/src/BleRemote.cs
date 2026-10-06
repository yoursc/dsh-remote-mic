using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DshRemoteMic
{
    /// <summary>
    /// BLE 连接管理 + ATVV 桥。
    ///
    /// 职责边界：这里只做「持有连接、订阅、转发原始字节」。ATVV 状态机、ADPCM 解码、
    /// WAV 组装全部留在浏览器侧 —— 那里已有 22 项测试覆盖，换 transport 即可复用。
    ///
    /// 关于配对：本程序【不主动触发配对】。未打包的桌面应用调用 PairAsync 时可能抛
    /// UnauthorizedAccessException，还可能弹系统同意框。用户已明确要与 Windows 自己的
    /// 配对流程对齐，所以这里只负责检测配对状态并直连，配对动作交给「设置 → 蓝牙」。
    /// </summary>
    internal sealed class BleRemote : IDisposable, IAtvvTransport
    {
        private string _lastErrorCode;

        /// <summary>
        /// 最近一次失败的**结构化**错误码（PROTOCOL.md §9.1）。成功时置 null。
        ///
        /// ⚠ 上层【禁止】用 Message 文本反推错误码 —— WinRT 异常消息是本地化的，
        /// 中文 Windows 上是中文。靠"包含 Authentication failed"判定会导致
        /// 英文系统正确、中文系统失效。这里在失败点就地记下结构化结论。
        /// </summary>
        public string LastErrorCode { get { return _lastErrorCode; } }

        /// <summary>记录结构化错误码并返回给调用方看的诊断文本。</summary>
        private string Fail(string code, string message)
        {
            _lastErrorCode = code;
            return message;
        }
        public event Action<byte[]> ControlReceived;
        public event Action<byte[]> AudioReceived;
        public event Action<bool> ConnectionChanged;
        public event Action<DeviceInfo> DeviceInfoReady;
        public event Action<int> BatteryChanged;   // 百分比，负数表示读不到

        private BluetoothLEDevice _device;
        private GattCharacteristic _transmit;
        private GattCharacteristic _audioChar;
        private GattCharacteristic _controlChar;
        private GattCharacteristic _batteryChar;
        private readonly object _sync = new object();
        private int _busy;

        // ---- 标准服务：型号与电量都走标准 GATT，不需要任何破解 ----
        // 2026-09-29 真机全枚举确认这两个服务都存在，且 Battery Level 属性是 Read + Notify。
        // （0x180A/0x180F 虽然在 Web Bluetooth 黑名单里，但那只是浏览器的限制，WinRT 本地不受限。）
        private static readonly Guid DisService = new Guid("0000180a-0000-1000-8000-00805f9b34fb");
        private static readonly Guid DisModel = new Guid("00002a24-0000-1000-8000-00805f9b34fb");
        private static readonly Guid DisManufacturer = new Guid("00002a29-0000-1000-8000-00805f9b34fb");
        private static readonly Guid DisFirmware = new Guid("00002a26-0000-1000-8000-00805f9b34fb");
        private static readonly Guid DisHardware = new Guid("00002a27-0000-1000-8000-00805f9b34fb");
        private static readonly Guid DisSerial = new Guid("00002a25-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryService = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevel = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

        public bool IsConnected
        {
            get
            {
                var d = _device;
                return d != null && d.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
        }

        public bool IsPaired
        {
            get
            {
                try
                {
                    var d = _device;
                    return d != null && d.DeviceInformation != null
                           && d.DeviceInformation.Pairing != null
                           && d.DeviceInformation.Pairing.IsPaired;
                }
                catch { return false; }
            }
        }

        public string DeviceName
        {
            get
            {
                try { return _device != null ? _device.Name : ""; }
                catch { return ""; }
            }
        }

        /// <summary>
        /// 按地址直连。已配对设备即使不广播也能连上（WinRT 的 FromBluetoothAddressAsync
        /// 不需要广播），这一点已由 Python 版探针在真机上验证。
        ///
        /// 外层这一圈是 2026-10-06 加的取证桩：每轮连接尝试落一条 kind="round" 事实行
        /// （判定层设计的原始数据：Radio 状态 / 注册表有无地址 / 卡在哪一步 / 各步耗时），
        /// 连接本体在 <see cref="ConnectCoreAsync"/>。
        /// </summary>
        public async Task<string> ConnectAsync(ulong address)
        {
            int n = System.Threading.Interlocked.Increment(ref _round);
            var clk = System.Diagnostics.Stopwatch.StartNew();
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
            {
                DebugLog.Event("round", "n=" + n + " skipped:busy");
                return "正在连接中";
            }
            var facts = new StringBuilder(192);
            facts.Append("n=").Append(n);
            try
            {
                facts.Append(" entryConn=").Append(IsConnected ? '1' : '0');
                facts.Append(" radio=").Append(await DebugLog.ProbeRadioAsync().ConfigureAwait(false));
                facts.Append(" reg=").Append(DebugLog.ProbePairedInRegistry(address));

                string r = await ConnectCoreAsync(address, facts).ConfigureAwait(false);
                facts.Append(" => ").Append(r == null ? "OK" : "FAIL/" + (_lastErrorCode ?? "?"));
                facts.Append(" total=").Append(clk.ElapsedMilliseconds).Append("ms");
                DebugLog.Event("round", facts.ToString());
                if (r != null)
                {
                    // 半失败必须净身出户：_device 挂着活链会让 IsConnected 恒真，
                    // tick 在「if (_ble.IsConnected) return;」就返回，重连循环脑死亡
                    // （2026-10-06 场景 1 实锤：char=AccessDenied 失败后 66 秒零尝试）。
                    DisposeDevice();
                }
                return r;
            }
            catch (Exception e)
            {
                facts.Append(" => CRASH/").Append(e.GetType().Name);
                facts.Append(" total=").Append(clk.ElapsedMilliseconds).Append("ms");
                DebugLog.Event("round", facts.ToString());
                DisposeDevice();   // 异常路径同样不留活链（理由同上）
                throw;
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static int _round;

        private async Task<string> ConnectCoreAsync(ulong address, StringBuilder facts)
        {
            DisposeDevice();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BluetoothLEDevice dev;
            try
            {
                dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                facts.Append(" create=throw/").Append(e.GetType().Name)
                     .Append('(').Append(sw.ElapsedMilliseconds).Append("ms)");
                return Fail("device_not_found", "创建设备对象失败：" + e.Message);
            }
            facts.Append(" create=").Append(dev != null ? "ok" : "null")
                 .Append('(').Append(sw.ElapsedMilliseconds).Append("ms)");
            if (dev == null) return Fail("device_not_found", "找不到该地址的设备（可能未配对或已断电）");

            _device = dev;
            try
            {
                facts.Append(" status=").Append(dev.ConnectionStatus);
                var di = dev.DeviceInformation;
                facts.Append(" paired=").Append(di != null && di.Pairing != null ? di.Pairing.IsPaired.ToString() : "?");
                facts.Append(" name=").Append(string.IsNullOrEmpty(dev.Name) ? "-" : dev.Name);
            }
            catch { facts.Append(" meta=throw"); }
            dev.ConnectionStatusChanged += OnConnectionStatusChanged;

            // FromBluetoothAddressAsync 返回对象不代表链路已建立，要等 ConnectionStatus
            if (dev.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                var tcs = new TaskCompletionSource<bool>();
                TypedEventHandler<BluetoothLEDevice, object> h = (s, e) =>
                {
                    if (s.ConnectionStatus == BluetoothConnectionStatus.Connected) tcs.TrySetResult(true);
                };
                dev.ConnectionStatusChanged += h;
                var timeout = Task.Delay(10000);
                sw.Restart();
                var done = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
                dev.ConnectionStatusChanged -= h;
                facts.Append(" wait=").Append(done == timeout ? "timeout" : "connected")
                     .Append('(').Append(sw.ElapsedMilliseconds).Append("ms)");
                if (done == timeout) return ConnFail("等待连接超时（10 秒）");
            }
            else facts.Append(" wait=skip(already)");

            sw.Restart();
            var svcResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            facts.Append(" svc=").Append(svcResult.Status)
                 .Append('(').Append(sw.ElapsedMilliseconds).Append("ms)");
            if (svcResult.Status != GattCommunicationStatus.Success)
                return ConnFail("枚举服务失败：" + svcResult.Status, svcResult.Status);

            return await OpenAtvvAsync(dev, svcResult.Services, facts).ConfigureAwait(false);
        }

        /// <summary>枚举服务、取三个特征、订阅 CONTROL 与 AUDIO，顺带读型号与电量。</summary>
        private async Task<string> OpenAtvvAsync(BluetoothLEDevice dev, IReadOnlyList<GattDeviceService> services, StringBuilder facts)
        {
            GattDeviceService svc = null;
            foreach (var s in services)
            {
                if (s.Uuid == Atvv.Service) { svc = s; break; }
            }
            facts.Append(" atvv=").Append(svc != null ? "found" : "missing");
            if (svc == null)
            {
                // 设备连上了但没有 ATVV 服务 —— 要么是别的设备，要么服务缓存没刷新
                var sb = new System.Text.StringBuilder("设备上没有 ATVV 服务。实际服务：");
                foreach (var s in services) sb.Append(s.Uuid.ToString().Substring(0, 8)).Append(' ');
                return Fail("unsupported_device", sb.ToString());
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var charResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            facts.Append(" char=").Append(charResult.Status)
                 .Append('(').Append(sw.ElapsedMilliseconds).Append("ms)");
            if (charResult.Status != GattCommunicationStatus.Success)
                return ConnFail("枚举特征失败：" + charResult.Status, charResult.Status);

            _transmit = null; _audioChar = null; _controlChar = null;
            foreach (var c in charResult.Characteristics)
            {
                if (c.Uuid == Atvv.Transmit) _transmit = c;
                else if (c.Uuid == Atvv.Audio) _audioChar = c;
                else if (c.Uuid == Atvv.Control) _controlChar = c;
            }
            if (_transmit == null || _audioChar == null || _controlChar == null)
                return Fail("unsupported_device", "ATVV 特征不全（0002/0003/0004 有缺失）");

            // 先订阅再让上层握手，避免「预按住键时软件未就绪」漏掉起始音频（AGENTS.md §7 坑 6）
            sw.Restart();
            var r1 = await SubscribeAsync(_controlChar, OnControlChanged).ConfigureAwait(false);
            if (r1 != null)
            {
                facts.Append(" sub=fail/control(").Append(sw.ElapsedMilliseconds).Append("ms)");
                return r1;
            }
            var r2 = await SubscribeAsync(_audioChar, OnAudioChanged).ConfigureAwait(false);
            if (r2 != null)
            {
                facts.Append(" sub=fail/audio(").Append(sw.ElapsedMilliseconds).Append("ms)");
                return r2;
            }
            facts.Append(" sub=ok(").Append(sw.ElapsedMilliseconds).Append("ms)");

            // 型号与电量是锦上添花：读失败绝不能把音频链路拖下水
            try { await ReadMetaAsync(services, dev).ConfigureAwait(false); }
            catch { }

            _lastErrorCode = null;
            RaiseConnectionChanged(true);
            return null;   // null = 成功
        }

        /// <summary>
        /// 读 Device Information（型号/厂商/固件/序列号）与 Battery Service（电量）。
        /// 服务清单是连接时已经枚举出来的，这里复用，不再多一次 GATT 往返。
        /// </summary>
        private async Task ReadMetaAsync(IReadOnlyList<GattDeviceService> services, BluetoothLEDevice dev)
        {
            GattDeviceService dis = null, bas = null;
            foreach (var s in services)
            {
                if (s.Uuid == DisService) dis = s;
                else if (s.Uuid == BatteryService) bas = s;
            }

            if (dis != null)
            {
                var chars = await CharacteristicsOfAsync(dis).ConfigureAwait(false);
                var info = DeviceProfiles.Identify(
                    await ReadStringAsync(chars, DisModel).ConfigureAwait(false),
                    await ReadStringAsync(chars, DisManufacturer).ConfigureAwait(false),
                    await ReadStringAsync(chars, DisFirmware).ConfigureAwait(false),
                    await ReadStringAsync(chars, DisHardware).ConfigureAwait(false),
                    await ReadStringAsync(chars, DisSerial).ConfigureAwait(false),
                    dev != null ? dev.Name : "");

                var h1 = DeviceInfoReady;
                if (h1 != null) h1(info);
            }

            if (bas != null)
            {
                var chars = await CharacteristicsOfAsync(bas).ConfigureAwait(false);
                GattCharacteristic c;
                if (chars.TryGetValue(BatteryLevel, out c))
                {
                    _batteryChar = c;
                    var level = await ReadBatteryAsync(c).ConfigureAwait(false);
                    if (level >= 0)
                    {
                        var h2 = BatteryChanged;
                        if (h2 != null) h2(level);
                    }

                    // Battery Level 支持 Notify（实测属性为 RN），订阅后不必轮询：
                    // 遥控器自己会在电量跨档变化时推过来。
                    try
                    {
                        var st = await c.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
                        if (st == GattCommunicationStatus.Success) c.ValueChanged += OnBatteryChanged;
                    }
                    catch { }
                }
            }
        }

        private void OnBatteryChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var bytes = ToArray(args.CharacteristicValue);
            if (bytes.Length == 0) return;
            var h = BatteryChanged;
            if (h != null) h(bytes[0]);
        }

        private static async Task<Dictionary<Guid, GattCharacteristic>> CharacteristicsOfAsync(GattDeviceService svc)
        {
            var map = new Dictionary<Guid, GattCharacteristic>();
            var r = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            if (r.Status == GattCommunicationStatus.Success)
            {
                foreach (var c in r.Characteristics) map[c.Uuid] = c;
            }
            return map;
        }

        private static async Task<string> ReadStringAsync(Dictionary<Guid, GattCharacteristic> chars, Guid uuid)
        {
            GattCharacteristic c;
            if (!chars.TryGetValue(uuid, out c)) return "";
            if (!c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) return "";
            try
            {
                var v = await c.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                if (v.Status != GattCommunicationStatus.Success) return "";
                var bytes = ToArray(v.Value);
                // DIS 字符串常带 NUL 填充，交给 DeviceProfiles.Clean 统一处理
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        private static async Task<int> ReadBatteryAsync(GattCharacteristic c)
        {
            try
            {
                var v = await c.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                if (v.Status != GattCommunicationStatus.Success) return -1;
                var bytes = ToArray(v.Value);
                return bytes.Length > 0 ? bytes[0] : -1;
            }
            catch { return -1; }
        }

        private async Task<string> SubscribeAsync(GattCharacteristic c,
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
        {
            try
            {
                var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
                if (status != GattCommunicationStatus.Success)
                    return ConnFail("订阅 " + Short(c.Uuid) + " 失败：" + status, status);
                c.ValueChanged += handler;
                _lastErrorCode = null;
                return null;
            }
            catch (Exception e)
            {
                // ATT 0x05（链路未加密）在 WinRT 上表现为 AccessDenied，
                // 而它的异常消息是本地化的 —— 所以这里只看配对状态这个结构化事实。
                return ConnFail("订阅 " + Short(c.Uuid) + " 异常：" + e.Message);
            }
        }

        /// <summary>连接期失败的判定：未配对是根因，其次才是超时。</summary>
        private string ConnFail(string message)
        {
            return Fail(!IsPaired ? "pairing_required" : "connect_timeout", message);
        }

        private string ConnFail(string message, GattCommunicationStatus status)
        {
            bool auth = status == GattCommunicationStatus.AccessDenied || !IsPaired;
            return Fail(auth ? "pairing_required" : "connect_timeout", message);
        }

        private static string Short(Guid g)
        {
            var s = g.ToString();
            return s.Substring(0, 8);
        }

        private void OnControlChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] bytes = ToArray(args.CharacteristicValue);
            if (bytes.Length == 0) return;
            // 在这一层落盘：连后面被 Arm 前过滤、被会话机忽略的通知也逃不掉。
            // 「松手后到底有没有 0x00」这种问题，只能靠这一层的全量记录回答。
            DebugLog.Control(bytes);
            var h = ControlReceived;
            if (h != null) h(bytes);
        }

        private void OnAudioChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] bytes = ToArray(args.CharacteristicValue);
            if (bytes.Length == 0) return;
            DebugLog.Audio(bytes);
            var h = AudioReceived;
            if (h != null) h(bytes);
        }

        private static byte[] ToArray(IBuffer buf)
        {
            if (buf == null) return new byte[0];
            var bytes = new byte[buf.Length];
            using (var reader = DataReader.FromBuffer(buf))
            {
                reader.ReadBytes(bytes);
            }
            return bytes;
        }

        /// <summary>写 TRANSMIT(0002)。成功返回 null，失败返回原因。</summary>
        public async Task<string> WriteAsync(byte[] data)
        {
            var c = _transmit;
            if (c == null)
            {
                string e1 = Fail("write_failed", "未连接");
                DebugLog.Write(data, e1);
                return e1;
            }
            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(data);
                var status = await c.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
                                   .AsTask().ConfigureAwait(false);
                if (status != GattCommunicationStatus.Success)
                {
                    string e2 = Fail("write_failed", "写入失败：" + status);
                    DebugLog.Write(data, e2);
                    return e2;
                }
                _lastErrorCode = null;
                DebugLog.Write(data, null);
                return null;
            }
            catch (Exception e)
            {
                string e3 = Fail("write_failed", "写入异常：" + e.Message);
                DebugLog.Write(data, e3);
                return e3;
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
            // 原始链路事件全量落盘：关蓝牙时 WinRT 到底发不发事件， absence 本身就是证据
            DebugLog.Event("link", "ConnectionStatusChanged -> " + sender.ConnectionStatus);
            RaiseConnectionChanged(connected);
            if (!connected)
            {
                DisposeDevice();
            }
        }

        private void RaiseConnectionChanged(bool connected)
        {
            var h = ConnectionChanged;
            if (h != null) h(connected);
        }

        public void Disconnect()
        {
            DisposeDevice();
            RaiseConnectionChanged(false);
        }

        private void DisposeDevice()
        {
            lock (_sync)
            {
                if (_controlChar != null) { try { _controlChar.ValueChanged -= OnControlChanged; } catch { } }
                if (_audioChar != null) { try { _audioChar.ValueChanged -= OnAudioChanged; } catch { } }
                if (_batteryChar != null) { try { _batteryChar.ValueChanged -= OnBatteryChanged; } catch { } }
                _transmit = null; _audioChar = null; _controlChar = null; _batteryChar = null;
                var d = _device;
                _device = null;
                if (d != null)
                {
                    try { d.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
                    try { d.Dispose(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            DisposeDevice();
        }
    }
}

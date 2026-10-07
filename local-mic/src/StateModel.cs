using System;

namespace DshRemoteMic
{
    /// <summary>
    /// local-mic 的无 IO 状态判定。采集层负责填充 Facts，转移层负责维护入口来源、账本与 t0。
    /// </summary>
    internal enum DeviceState
    {
        BLE_NotExist,
        BLE_Off,
        DeviceNotSelected,
        BLE_Unpaired,
        Connecting,
        Unresponsive,
        Connected
    }

    internal enum ConnectionAttemptKind
    {
        Entry,
        Retry
    }

    internal sealed class StateFacts
    {
        public bool HasAddress;
        public bool? BluetoothRadioAvailable;
        public bool? BluetoothRadioOn;
        public bool RegistryPaired;
        public bool LinkConnected;
        public ConnectionAttemptKind AttemptKind;
        public DateTime AttemptStartedUtc;
        public int LinkFailures;
        public bool GattOpening;
        public DateTime GattStartedUtc;
    }

    internal static class DeviceStateModel
    {
        public static DeviceState Decide(StateFacts f, DateTime nowUtc)
        {
            if (f.BluetoothRadioAvailable == false) return DeviceState.BLE_NotExist;
            if (f.BluetoothRadioOn == false) return DeviceState.BLE_Off;
            if (!f.HasAddress) return DeviceState.DeviceNotSelected;
            if (!f.RegistryPaired) return DeviceState.BLE_Unpaired;
            if (f.LinkConnected && !f.GattOpening) return DeviceState.Connected;

            if (f.GattOpening)
            {
                if (f.GattStartedUtc == default(DateTime) || nowUtc - f.GattStartedUtc < TimeSpan.FromSeconds(60))
                    return DeviceState.Connecting;
                return DeviceState.Unresponsive;
            }

            if (f.AttemptKind == ConnectionAttemptKind.Entry)
            {
                return f.AttemptStartedUtc != default(DateTime) &&
                       nowUtc - f.AttemptStartedUtc >= TimeSpan.FromSeconds(20)
                    ? DeviceState.Unresponsive : DeviceState.Connecting;
            }

            return f.LinkFailures >= 3 ? DeviceState.Unresponsive : DeviceState.Connecting;
        }

        public static string ProtocolState(DeviceState state)
        {
            switch (state)
            {
                case DeviceState.Connected: return "connected";
                case DeviceState.Connecting: return "connecting";
                default: return "error";
            }
        }

        public static string ErrorCode(DeviceState state)
        {
            switch (state)
            {
                case DeviceState.BLE_Unpaired: return "pairing_required";
                case DeviceState.Unresponsive: return "connect_timeout";
                case DeviceState.BLE_NotExist:
                case DeviceState.BLE_Off:
                case DeviceState.DeviceNotSelected: return "device_not_found";
                default: return null;
            }
        }

        public static bool Retryable(DeviceState state)
        {
            return state != DeviceState.BLE_Unpaired && state != DeviceState.DeviceNotSelected;
        }

        internal static string RunSelfTest()
        {
            var now = DateTime.UtcNow;
            var baseFacts = new StateFacts { HasAddress = true, RegistryPaired = true,
                BluetoothRadioAvailable = true, BluetoothRadioOn = true,
                AttemptKind = ConnectionAttemptKind.Entry, AttemptStartedUtc = now };
            if (Decide(new StateFacts { BluetoothRadioAvailable = false }, now) != DeviceState.BLE_NotExist)
                return "状态模型自检失败：BLE_NotExist";
            if (Decide(new StateFacts { BluetoothRadioAvailable = true, BluetoothRadioOn = false }, now) != DeviceState.BLE_Off)
                return "状态模型自检失败：BLE_Off";
            if (Decide(new StateFacts(), now) != DeviceState.DeviceNotSelected)
                return "状态模型自检失败：DeviceNotSelected";
            baseFacts.LinkConnected = true;
            if (Decide(baseFacts, now) != DeviceState.Connected)
                return "状态模型自检失败：Connected";
            baseFacts.LinkConnected = false;
            if (Decide(baseFacts, now) != DeviceState.Connecting)
                return "状态模型自检失败：Connecting";
            baseFacts.AttemptStartedUtc = now.AddSeconds(-21);
            if (Decide(baseFacts, now) != DeviceState.Unresponsive)
                return "状态模型自检失败：入口超时";
            baseFacts.AttemptKind = ConnectionAttemptKind.Retry;
            baseFacts.AttemptStartedUtc = now;
            baseFacts.LinkFailures = 3;
            if (Decide(baseFacts, now) != DeviceState.Unresponsive)
                return "状态模型自检失败：fd 阈值";
            baseFacts.GattOpening = true;
            baseFacts.GattStartedUtc = now.AddSeconds(-61);
            baseFacts.LinkFailures = 0;
            if (Decide(baseFacts, now) != DeviceState.Unresponsive)
                return "状态模型自检失败：gd 超时";
            return "状态模型自检通过：8/8 条";
        }
    }
}

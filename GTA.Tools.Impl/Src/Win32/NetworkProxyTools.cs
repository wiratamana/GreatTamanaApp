
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace GTA.Tools.Impl.Win32;

public static class NetworkProxyTools
{

    public static bool TryChangeWifi(string ssid, out string result, int timeoutMs = 20000)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            result = "InvalidArgument: 'ssid' was null/empty/whitespace — supply the exact Wi-Fi network name to connect to.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — Wi-Fi connect via the wlanapi.dll P/Invoke Native Wifi API is Windows-only.";
            return false;
        }

        IntPtr clientHandle = IntPtr.Zero;

        try
        {
            int openResult = Wlan.WlanOpenHandle(Wlan.WLAN_CLIENT_VERSION_VISTA_AND_LATER, IntPtr.Zero, out _, out clientHandle);
            if (openResult != Wlan.ERROR_SUCCESS)
            {
                result = $"NoWirelessAdapter: WlanOpenHandle failed (code {openResult}) — the WLAN AutoConfig service may be disabled, or there is no Wi-Fi hardware on this machine.";
                return false;
            }

            List<Guid> interfaceGuids = EnumerateWirelessInterfaceGuids(clientHandle);
            if (interfaceGuids.Count == 0)
            {
                result = "NoWirelessAdapter: no wireless network interfaces were found on this machine (WlanEnumInterfaces returned zero interfaces).";
                return false;
            }

            Guid interfaceGuid = interfaceGuids[0];

            var connectionParameters = new Wlan.WLAN_CONNECTION_PARAMETERS
            {
                wlanConnectionMode = Wlan.WLAN_CONNECTION_MODE.wlan_connection_mode_profile,
                strProfile = ssid,
                pDot11Ssid = IntPtr.Zero,
                pDesiredBssidList = IntPtr.Zero,
                dot11BssType = Wlan.DOT11_BSS_TYPE.dot11_BSS_type_any,
                dwFlags = 0,
            };

            int connectResult = Wlan.WlanConnect(clientHandle, ref interfaceGuid, ref connectionParameters, IntPtr.Zero);
            if (connectResult == Wlan.ERROR_NOT_FOUND)
            {
                result = $"ProfileNotFound: no saved Wi-Fi profile named \"{ssid}\" exists on this machine. " +
                         "Windows cannot supply a password for a secured network it has never joined before — " +
                         $"connect to \"{ssid}\" manually once (enter its password in the normal Wi-Fi picker) so a profile gets saved, then retry.";
                return false;
            }

            if (connectResult != Wlan.ERROR_SUCCESS)
            {
                result = $"Error: WlanConnect failed with native error code {connectResult} while requesting a connection to \"{ssid}\".";
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            string? lastObservedSsid = null;
            Wlan.WLAN_INTERFACE_STATE lastObservedState = Wlan.WLAN_INTERFACE_STATE.NotReady;

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(1000);

                if (TryGetCurrentConnection(clientHandle, interfaceGuid, out string? currentSsid, out Wlan.WLAN_INTERFACE_STATE state))
                {
                    lastObservedSsid = currentSsid;
                    lastObservedState = state;

                    if (state == Wlan.WLAN_INTERFACE_STATE.Connected && string.Equals(currentSsid, ssid, StringComparison.OrdinalIgnoreCase))
                    {
                        result = $"Connected: successfully connected to Wi-Fi network \"{ssid}\" (confirmed via WlanQueryInterface after {stopwatch.Elapsed.TotalSeconds:0.0}s).";
                        return true;
                    }
                }
            }

            result = $"Timeout: connect request to \"{ssid}\" was accepted but did not complete within {timeoutMs}ms. " +
                     $"Last observed state: {lastObservedState}, last observed SSID: \"{lastObservedSsid ?? "(none)"}\". " +
                     "The connection may still complete shortly after this call returns (e.g. slow captive-portal/802.1X auth) — consider retrying TryGetCurrentWifiSsid after a few more seconds, or re-calling this method with a larger timeoutMs.";
            return false;
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while changing Wi-Fi to \"{ssid}\": {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (clientHandle != IntPtr.Zero)
            {
                Wlan.WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
    }

    public static bool TrySetProxy(bool enable, out string result)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — the proxy setting lives in the Windows registry (Internet Settings) and is Windows-only.";
            return false;
        }

        string desired = enable ? "On" : "Off";

        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(InternetSettingsKeyPath, writable: true);
            if (key == null)
            {
                result = $"Error: could not open/create registry key \"HKCU\\{InternetSettingsKeyPath}\" for writing (CreateSubKey returned null).";
                return false;
            }

            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);

            WinInet.InternetSetOption(IntPtr.Zero, WinInet.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            WinInet.InternetSetOption(IntPtr.Zero, WinInet.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);

            if (!TryGetProxyStatus(out string confirmedStatus))
            {
                result = $"Error: wrote ProxyEnable={(enable ? 1 : 0)} to the registry but could not re-read it back afterwards to confirm ({confirmedStatus}).";
                return false;
            }

            if (!string.Equals(confirmedStatus, desired, StringComparison.OrdinalIgnoreCase))
            {
                result = $"Error: requested proxy {desired}, wrote the registry value, but re-reading it back afterwards shows \"{confirmedStatus}\" instead — the change may have been overwritten by policy/another process.";
                return false;
            }

            result = $"Success: proxy is now confirmed {desired} (registry ProxyEnable={(enable ? 1 : 0)}, WinINet notified of the change).";
            return true;
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while setting proxy to {desired}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static bool TryGetNetworkType(out string result)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — network type detection via GetAdaptersAddresses P/Invoke is Windows-only.";
            return false;
        }

        if (!Environment.Is64BitProcess)
        {
            result = "PlatformNotSupported: running as a 32-bit process — the IP_ADAPTER_ADDRESSES P/Invoke layout used here assumes a 64-bit process. Rebuild/run this as a 64-bit process.";
            return false;
        }

        try
        {
            const uint flags = IpHlp.GAA_FLAG_SKIP_ANYCAST | IpHlp.GAA_FLAG_SKIP_MULTICAST | IpHlp.GAA_FLAG_SKIP_DNS_SERVER;

            uint size = 0;
            int sizeResult = IpHlp.GetAdaptersAddresses(IpHlp.AF_UNSPEC, flags, IntPtr.Zero, IntPtr.Zero, ref size);

            if (sizeResult == IpHlp.ERROR_NO_DATA)
            {
                result = "None: GetAdaptersAddresses reports no network adapters at all on this machine.";
                return true;
            }

            if (sizeResult != IpHlp.ERROR_SUCCESS && sizeResult != IpHlp.ERROR_BUFFER_OVERFLOW)
            {
                result = $"Error: GetAdaptersAddresses (size query) failed with native code {sizeResult}.";
                return false;
            }

            if (size == 0)
            {
                result = "None: GetAdaptersAddresses reported a zero-size buffer (no adapters).";
                return true;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                int dataResult = IpHlp.GetAdaptersAddresses(IpHlp.AF_UNSPEC, flags, IntPtr.Zero, buffer, ref size);

                if (dataResult == IpHlp.ERROR_NO_DATA)
                {
                    result = "None: GetAdaptersAddresses reports no network adapters at all on this machine.";
                    return true;
                }

                if (dataResult != IpHlp.ERROR_SUCCESS)
                {
                    result = $"Error: GetAdaptersAddresses failed with native code {dataResult}.";
                    return false;
                }

                bool wiredUp = false;
                bool wirelessUp = false;

                IntPtr current = buffer;
                while (current != IntPtr.Zero)
                {
                    IpHlp.IP_ADAPTER_ADDRESSES adapter = Marshal.PtrToStructure<IpHlp.IP_ADAPTER_ADDRESSES>(current);
                    bool isUp = adapter.OperStatus == IpHlp.IF_OPER_STATUS_UP;

                    if (isUp)
                    {
                        if (adapter.IfType == IpHlp.IF_TYPE_ETHERNET_CSMACD) wiredUp = true;
                        else if (adapter.IfType == IpHlp.IF_TYPE_IEEE80211) wirelessUp = true;
                    }

                    current = adapter.Next;
                }

                if (wiredUp)
                {
                    result = "Wired: a physical Ethernet adapter is up (cable connected).";
                    return true;
                }

                if (wirelessUp)
                {
                    result = "Wireless: a Wi-Fi (802.11) adapter is up (associated to a network).";
                    return true;
                }

                result = "None: neither a wired nor a wireless adapter currently reports an Up status.";
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while detecting network type: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static bool TryGetProxyStatus(out string result)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — the proxy setting lives in the Windows registry (Internet Settings) and is Windows-only.";
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath);
            if (key == null)
            {
                // Key not present at all -> proxy has never been configured -> effectively OFF.
                result = "Off";
                return true;
            }

            object? value = key.GetValue("ProxyEnable");
            if (value == null)
            {
                result = "Off";
                return true;
            }

            int enabled = Convert.ToInt32(value);
            result = enabled != 0 ? "On" : "Off";
            return true;
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while reading proxy status: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static bool TryGetProxyDetails(out string result)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — the proxy setting lives in the Windows registry (Internet Settings) and is Windows-only.";
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath);
            if (key == null)
            {
                result = "Off (no ProxyServer/ProxyOverride configured — Internet Settings key not found).";
                return true;
            }

            object? enableValue = key.GetValue("ProxyEnable");
            string status = enableValue != null && Convert.ToInt32(enableValue) != 0 ? "On" : "Off";
            string proxyServer = key.GetValue("ProxyServer") as string ?? "(none)";
            string proxyOverride = key.GetValue("ProxyOverride") as string ?? "(none)";

            result = $"{status} (ProxyServer=\"{proxyServer}\", ProxyOverride=\"{proxyOverride}\")";
            return true;
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while reading proxy details: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static bool TryGetCurrentWifiSsid(out string result)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — Wi-Fi status via the wlanapi.dll P/Invoke Native Wifi API is Windows-only.";
            return false;
        }

        IntPtr clientHandle = IntPtr.Zero;

        try
        {
            int openResult = Wlan.WlanOpenHandle(Wlan.WLAN_CLIENT_VERSION_VISTA_AND_LATER, IntPtr.Zero, out _, out clientHandle);
            if (openResult != Wlan.ERROR_SUCCESS)
            {
                result = $"NoWirelessAdapter: WlanOpenHandle failed (code {openResult}) — the WLAN AutoConfig service may be disabled, or there is no Wi-Fi hardware on this machine.";
                return false;
            }

            List<Guid> interfaceGuids = EnumerateWirelessInterfaceGuids(clientHandle);
            if (interfaceGuids.Count == 0)
            {
                result = "NoWirelessAdapter: no wireless network interfaces were found on this machine (WlanEnumInterfaces returned zero interfaces).";
                return false;
            }

            if (TryGetCurrentConnection(clientHandle, interfaceGuids[0], out string? ssid, out Wlan.WLAN_INTERFACE_STATE state) && state == Wlan.WLAN_INTERFACE_STATE.Connected)
            {
                result = $"Connected: SSID=\"{ssid}\"";
                return true;
            }

            result = $"NotConnected: the wireless adapter is not currently associated to any network (state={state}).";
            return true;
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while reading current Wi-Fi SSID: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (clientHandle != IntPtr.Zero)
            {
                Wlan.WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
    }

    public static bool TryGetAvailableWifiNetworks(out string result, int scanWaitMs = 3000)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = "PlatformNotSupported: this is not a Windows machine — Wi-Fi scanning via the wlanapi.dll P/Invoke Native Wifi API is Windows-only.";
            return false;
        }

        IntPtr clientHandle = IntPtr.Zero;

        try
        {
            int openResult = Wlan.WlanOpenHandle(Wlan.WLAN_CLIENT_VERSION_VISTA_AND_LATER, IntPtr.Zero, out _, out clientHandle);
            if (openResult != Wlan.ERROR_SUCCESS)
            {
                result = $"NoWirelessAdapter: WlanOpenHandle failed (code {openResult}) — the WLAN AutoConfig service may be disabled, or there is no Wi-Fi hardware on this machine.";
                return false;
            }

            List<Guid> interfaceGuids = EnumerateWirelessInterfaceGuids(clientHandle);
            if (interfaceGuids.Count == 0)
            {
                result = "NoWirelessAdapter: no wireless network interfaces were found on this machine (WlanEnumInterfaces returned zero interfaces).";
                return false;
            }

            foreach (Guid guid in interfaceGuids)
            {
                Guid localGuid = guid;
                _ = Wlan.WlanScan(clientHandle, ref localGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }

            if (scanWaitMs > 0)
            {
                Thread.Sleep(scanWaitMs);
            }

            var lines = new List<string>();
            foreach (Guid guid in interfaceGuids)
            {
                Guid localGuid = guid;
                lines.AddRange(GetAvailableNetworkLinesForInterface(clientHandle, localGuid));
            }

            result = lines.Count == 0
                ? "No Wi-Fi networks were found."
                : string.Join(Environment.NewLine, lines.Distinct());
            return true;
        }
        catch (Exception ex)
        {
            result = $"Error: unexpected exception while scanning for Wi-Fi networks: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (clientHandle != IntPtr.Zero)
            {
                Wlan.WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
    }

    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static bool TryGetCurrentConnection(IntPtr clientHandle, Guid interfaceGuid, out string? ssid, out Wlan.WLAN_INTERFACE_STATE state)
    {
        ssid = null;
        state = Wlan.WLAN_INTERFACE_STATE.NotReady;

        int result = Wlan.WlanQueryInterface(
            clientHandle,
            ref interfaceGuid,
            Wlan.WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
            IntPtr.Zero,
            out _,
            out IntPtr dataPtr,
            IntPtr.Zero);

        if (result != Wlan.ERROR_SUCCESS || dataPtr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            Wlan.WLAN_CONNECTION_ATTRIBUTES attributes = Marshal.PtrToStructure<Wlan.WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
            state = attributes.isState;
            ssid = DecodeSsid(attributes.AssociationAttributes.dot11Ssid);
            return true;
        }
        finally
        {
            Wlan.WlanFreeMemory(dataPtr);
        }
    }

    private static List<Guid> EnumerateWirelessInterfaceGuids(IntPtr clientHandle)
    {
        var guids = new List<Guid>();

        int result = Wlan.WlanEnumInterfaces(clientHandle, IntPtr.Zero, out IntPtr interfaceListPtr);
        if (result != Wlan.ERROR_SUCCESS || interfaceListPtr == IntPtr.Zero)
        {
            return guids;
        }

        try
        {
            uint numberOfItems = (uint)Marshal.ReadInt32(interfaceListPtr, 0);
            int headerSize = sizeof(uint) * 2;
            int itemSize = Marshal.SizeOf<Wlan.WLAN_INTERFACE_INFO>();

            for (int i = 0; i < numberOfItems; i++)
            {
                IntPtr itemPtr = IntPtr.Add(interfaceListPtr, headerSize + (i * itemSize));
                Wlan.WLAN_INTERFACE_INFO info = Marshal.PtrToStructure<Wlan.WLAN_INTERFACE_INFO>(itemPtr);
                guids.Add(info.InterfaceGuid);
            }
        }
        finally
        {
            Wlan.WlanFreeMemory(interfaceListPtr);
        }

        return guids;
    }

    private static List<string> GetAvailableNetworkLinesForInterface(IntPtr clientHandle, Guid interfaceGuid)
    {
        var lines = new List<string>();

        int result = Wlan.WlanGetAvailableNetworkList(clientHandle, ref interfaceGuid, 0, IntPtr.Zero, out IntPtr networkListPtr);
        if (result != Wlan.ERROR_SUCCESS || networkListPtr == IntPtr.Zero)
        {
            return lines;
        }

        try
        {
            uint numberOfItems = (uint)Marshal.ReadInt32(networkListPtr, 0);
            int headerSize = sizeof(uint) * 2;
            int itemSize = Marshal.SizeOf<Wlan.WLAN_AVAILABLE_NETWORK>();

            for (int i = 0; i < numberOfItems; i++)
            {
                IntPtr itemPtr = IntPtr.Add(networkListPtr, headerSize + (i * itemSize));
                Wlan.WLAN_AVAILABLE_NETWORK network = Marshal.PtrToStructure<Wlan.WLAN_AVAILABLE_NETWORK>(itemPtr);

                string ssid = DecodeSsid(network.dot11Ssid);
                if (string.IsNullOrEmpty(ssid))
                {
                    continue;
                }

                string secured = network.bSecurityEnabled != 0 ? "secured" : "open";
                lines.Add($"{ssid} ({network.wlanSignalQuality}%, {secured})");
            }
        }
        finally
        {
            Wlan.WlanFreeMemory(networkListPtr);
        }

        return lines;
    }

    private static string DecodeSsid(Wlan.DOT11_SSID ssid)
    {
        if (ssid.uSSIDLength == 0 || ssid.ucSSID == null)
        {
            return string.Empty;
        }

        int length = (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length);
        return Encoding.UTF8.GetString(ssid.ucSSID, 0, length);
    }

    /// <summary>IP Helper API (iphlpapi.dll) — wired/wireless "is it up" detection. 64-bit-process-only (raw pointer fields).</summary>
    private static class IpHlp
    {
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_BUFFER_OVERFLOW = 111;
        internal const int ERROR_NO_DATA = 232;

        internal const uint AF_UNSPEC = 0;

        internal const uint GAA_FLAG_SKIP_ANYCAST = 0x0002;
        internal const uint GAA_FLAG_SKIP_MULTICAST = 0x0004;
        internal const uint GAA_FLAG_SKIP_DNS_SERVER = 0x0008;

        internal const uint IF_TYPE_ETHERNET_CSMACD = 6;
        internal const uint IF_TYPE_IEEE80211 = 71;

        internal const uint IF_OPER_STATUS_UP = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern int GetAdaptersAddresses(
            uint family,
            uint flags,
            IntPtr reserved,
            IntPtr adapterAddresses,
            ref uint sizePointer);

        [StructLayout(LayoutKind.Sequential)]
        internal struct IP_ADAPTER_ADDRESSES
        {
            public uint Length;
            public uint IfIndex;
            public IntPtr Next;
            public IntPtr AdapterName;
            public IntPtr FirstUnicastAddress;
            public IntPtr FirstAnycastAddress;
            public IntPtr FirstMulticastAddress;
            public IntPtr FirstDnsServerAddress;
            public IntPtr DnsSuffix;
            public IntPtr Description;
            public IntPtr FriendlyName;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] PhysicalAddress;

            public uint PhysicalAddressLength;
            public uint Flags;
            public uint Mtu;
            public uint IfType;
            public uint OperStatus;
            public uint Ipv6IfIndex;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public uint[] ZoneIndices;

            public IntPtr FirstPrefix;
        }
    }

    /// <summary>Native Wifi API (wlanapi.dll) — enumerate/scan/connect/query Wi-Fi. No pointer fields, so 32/64-bit safe.</summary>
    private static class Wlan
    {
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_NOT_FOUND = 1168;

        internal const uint WLAN_CLIENT_VERSION_VISTA_AND_LATER = 2;

        [DllImport("wlanapi.dll")]
        internal static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        internal static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        internal static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        internal static extern int WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

        [DllImport("wlanapi.dll")]
        internal static extern int WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr pMemory);

        [DllImport("wlanapi.dll")]
        internal static extern int WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        internal static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        internal enum WLAN_INTF_OPCODE : uint
        {
            wlan_intf_opcode_current_connection = 7,
        }

        internal enum WLAN_CONNECTION_MODE : uint
        {
            wlan_connection_mode_profile = 0,
            wlan_connection_mode_temporary_profile = 1,
            wlan_connection_mode_discovery_secure = 2,
            wlan_connection_mode_discovery_unsecure = 3,
            wlan_connection_mode_auto = 4,
            wlan_connection_mode_invalid = 5,
        }

        internal enum DOT11_BSS_TYPE : uint
        {
            dot11_BSS_type_infrastructure = 1,
            dot11_BSS_type_independent = 2,
            dot11_BSS_type_any = 3,
        }

        internal enum WLAN_INTERFACE_STATE : uint
        {
            NotReady = 0,
            Connected = 1,
            AdHocNetworkFormed = 2,
            Disconnecting = 3,
            Disconnected = 4,
            Associating = 5,
            Discovering = 6,
            Authenticating = 7,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;

            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DOT11_SSID
        {
            public uint uSSIDLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;

            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            public uint uNumberOfBssids;
            public int bNetworkConnectable;
            public uint wlanNotConnectableReason;
            public uint uNumberOfPhyTypes;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] dot11PhyTypes;

            public int bMorePhyTypes;
            public uint wlanSignalQuality;
            public int bSecurityEnabled;
            public uint dot11DefaultAuthAlgorithm;
            public uint dot11DefaultCipherAlgorithm;
            public uint dwFlags;
            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_CONNECTION_PARAMETERS
        {
            public WLAN_CONNECTION_MODE wlanConnectionMode;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string strProfile;

            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public DOT11_BSS_TYPE dot11BssType;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;

            public uint dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_SECURITY_ATTRIBUTES
        {
            public int bSecurityEnabled;
            public int bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public WLAN_CONNECTION_MODE wlanConnectionMode;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;

            public WLAN_ASSOCIATION_ATTRIBUTES AssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES SecurityAttributes;
        }
    }

    /// <summary>WinINet (wininet.dll) — notify Windows that Internet Settings changed so the new proxy config takes effect immediately.</summary>
    private static class WinInet
    {
        internal const int INTERNET_OPTION_REFRESH = 37;
        internal const int INTERNET_OPTION_SETTINGS_CHANGED = 39;

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    }
}

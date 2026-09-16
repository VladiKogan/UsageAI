using System.Runtime.InteropServices;

namespace UsageAI.Services;

/// <summary>
/// Maps a process id to the TCP ports it listens on, read straight from the IP Helper API.
/// The Antigravity probe needs this on every Gemini refresh, and the obvious alternative —
/// shelling out to <c>Get-NetTCPConnection</c> — costs well over a second each time because
/// PowerShell has to start and autoload NetTCPIP. This answers in well under a millisecond.
/// </summary>
internal static class ListeningPortTable
{
    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>A table larger than this is treated as hostile rather than allocated.</summary>
    private const int MaxTableBytes = 8 * 1024 * 1024;

    /// <summary>Retries for the size-then-read race when the table grows between the two calls.</summary>
    private const int MaxSizeAttempts = 4;

    private const int Ipv4RowBytes = 24;
    private const int Ipv4RowLocalPortOffset = 8;
    private const int Ipv4RowOwningPidOffset = 20;

    private const int Ipv6RowBytes = 56;
    private const int Ipv6RowLocalPortOffset = 20;
    private const int Ipv6RowOwningPidOffset = 52;

    /// <summary>Listening TCP ports owned by <paramref name="pid"/>, across IPv4 and IPv6.</summary>
    public static IReadOnlyList<ushort> ForProcess(uint pid)
    {
        var ports = new List<ushort>();
        Collect(pid, AddressFamilyInterNetwork, Ipv4RowBytes, Ipv4RowLocalPortOffset, Ipv4RowOwningPidOffset, ports);
        Collect(pid, AddressFamilyInterNetworkV6, Ipv6RowBytes, Ipv6RowLocalPortOffset, Ipv6RowOwningPidOffset, ports);
        return ports;
    }

    private static void Collect(
        uint pid,
        int addressFamily,
        int rowBytes,
        int portOffset,
        int pidOffset,
        List<ushort> ports)
    {
        var size = 0;
        var table = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < MaxSizeAttempts; attempt++)
            {
                var status = GetExtendedTcpTable(
                    table,
                    ref size,
                    false,
                    addressFamily,
                    TcpTableOwnerPidListener,
                    0);
                if (status == ErrorSuccess && table != IntPtr.Zero)
                {
                    ReadRows(table, size, pid, rowBytes, portOffset, pidOffset, ports);
                    return;
                }

                if (status != ErrorInsufficientBuffer || size is <= 0 or > MaxTableBytes)
                {
                    return;
                }

                if (table != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(table);
                    table = IntPtr.Zero;
                }

                table = Marshal.AllocHGlobal(size);
            }
        }
        catch (OutOfMemoryException)
        {
            // A port list is never worth failing a refresh over; the caller falls back to discovery.
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(table);
            }
        }
    }

    private static void ReadRows(
        IntPtr table,
        int size,
        uint pid,
        int rowBytes,
        int portOffset,
        int pidOffset,
        List<ushort> ports)
    {
        var entries = Marshal.ReadInt32(table);
        if (entries <= 0)
        {
            return;
        }

        // The buffer the API filled is authoritative over its own entry count.
        var capacity = (size - sizeof(int)) / rowBytes;
        entries = Math.Min(entries, capacity);

        for (var index = 0; index < entries; index++)
        {
            var row = table + sizeof(int) + (index * rowBytes);
            if ((uint)Marshal.ReadInt32(row, pidOffset) != pid)
            {
                continue;
            }

            var port = ToHostPort(Marshal.ReadInt32(row, portOffset));
            if (port != 0 && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }
    }

    /// <summary>The table stores the port in network byte order inside the low word.</summary>
    internal static ushort ToHostPort(int networkOrderPort) =>
        (ushort)(((networkOrderPort & 0xFF) << 8) | ((networkOrderPort >> 8) & 0xFF));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        int reserved);
}

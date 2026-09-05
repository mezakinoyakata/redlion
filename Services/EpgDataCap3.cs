using System.Runtime.InteropServices;

namespace EDCBViewer.Services;

/// <summary>
/// EDCB 純正の EpgDataCap3.dll を直接呼び出して、*_epg.dat(TSパケットの生ストリーム)
/// から EPG を取り出す。
///
/// EpgTimerSrv は使わない。EDCB のプロセスを一切起動せず、ファイルと DLL だけで完結する。
/// (EIT の解析と ARIB 8単位符号のデコードは DLL が行うので、自前実装は不要)
///
/// 構造体は Common/EpgDataCap3Def.h の定義に対応する。
/// </summary>
public sealed class EpgDataCap3 : IDisposable
{
    private const string Dll = "EpgDataCap3.dll";

    /// <summary>
    /// EDCB の成功コード。ErrDef.h では NO_ERR = TRUE = 1 で、0(ERR_FALSE)が失敗。
    /// 一般的な「0 が成功」とは逆なので注意。
    /// </summary>
    private const uint NoErr = 1;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern uint InitializeEP([MarshalAs(UnmanagedType.Bool)] bool asyncFlag, out uint id);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern uint UnInitializeEP(uint id);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern uint AddTSPacketEP(uint id, byte[] data, uint size);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern uint GetServiceListEpgDBEP(uint id, out uint listSize, out IntPtr list);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern uint GetEpgInfoListEP(uint id, ushort onid, ushort tsid, ushort sid,
                                                out uint listSize, out IntPtr list);

    // ─── ネイティブ構造体 ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceExtInfo
    {
        public byte service_type;
        public byte partialReceptionFlag;
        public IntPtr service_provider_name;
        public IntPtr service_name;
        public IntPtr network_name;
        public IntPtr ts_name;
        public byte remote_control_key_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceInfo
    {
        public ushort original_network_id;
        public ushort transport_stream_id;
        public ushort service_id;
        public IntPtr extInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;

        public DateTime? ToDateTime()
        {
            if (wYear == 0) return null;
            try { return new DateTime(wYear, wMonth, wDay, wHour, wMinute, wSecond); }
            catch { return null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeShortEvent
    {
        public ushort event_nameLength;
        public IntPtr event_name;
        public ushort text_charLength;
        public IntPtr text_char;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeExtendedEvent
    {
        public ushort text_charLength;
        public IntPtr text_char;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeContent
    {
        public byte content_nibble_level_1;
        public byte content_nibble_level_2;
        public byte user_nibble_1;
        public byte user_nibble_2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeContentInfo
    {
        public ushort listSize;
        public IntPtr nibbleList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeComponentInfo
    {
        public byte stream_content;
        public byte component_type;
        public byte component_tag;
        public ushort text_charLength;
        public IntPtr text_char;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEventInfo
    {
        public ushort event_id;
        public byte StartTimeFlag;
        public SystemTime start_time;
        public byte DurationFlag;
        public uint durationSec;
        public IntPtr shortInfo;
        public IntPtr extInfo;
        public IntPtr contentInfo;
        public IntPtr componentInfo;
        public IntPtr audioInfo;
        public IntPtr eventGroupInfo;
        public IntPtr eventRelayInfo;
        public byte freeCAFlag;
    }

    // ─── 実装 ────────────────────────────────────────────────────────────────

    private uint _id;
    private bool _disposed;

    public EpgDataCap3()
    {
        uint ret = InitializeEP(false, out _id);
        if (ret != NoErr) throw new InvalidOperationException($"InitializeEP 失敗: {ret}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnInitializeEP(_id);
        _disposed = true;
    }

    /// <summary>*_epg.dat を読み込ませる。中身は 188 バイト単位の TS パケット。</summary>
    public void LoadFile(string path)
    {
        const int packet = 188;
        var buf = new byte[packet * 256];
        // 録画機の共有を直接読むため、EDCB が書き込み中のファイルでも開けるようにする
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            // 端数は切り捨てる(TS パケット境界でのみ渡す)
            int usable = n - (n % packet);
            if (usable <= 0) break;
            AddTSPacketEP(_id, buf, (uint)usable);
        }
    }

    public List<EpgService> GetServices()
    {
        var result = new List<EpgService>();
        if (GetServiceListEpgDBEP(_id, out uint size, out IntPtr list) != NoErr || list == IntPtr.Zero)
            return result;

        int stride = Marshal.SizeOf<NativeServiceInfo>();
        for (int i = 0; i < size; i++)
        {
            var s = Marshal.PtrToStructure<NativeServiceInfo>(list + i * stride);
            var item = new EpgService { Onid = s.original_network_id, Tsid = s.transport_stream_id, Sid = s.service_id };
            if (s.extInfo != IntPtr.Zero)
            {
                var e = Marshal.PtrToStructure<NativeServiceExtInfo>(s.extInfo);
                item.ServiceType      = e.service_type;
                item.PartialReception = e.partialReceptionFlag;
                item.RemoteControlKey = e.remote_control_key_id;
                item.ProviderName     = Str(e.service_provider_name);
                item.ServiceName      = Str(e.service_name);
                item.NetworkName      = Str(e.network_name);
                item.TsName           = Str(e.ts_name);
            }
            result.Add(item);
        }
        return result;
    }

    public List<EpgEventRow> GetEvents(EpgService svc)
    {
        var result = new List<EpgEventRow>();
        if (GetEpgInfoListEP(_id, svc.Onid, svc.Tsid, svc.Sid, out uint size, out IntPtr list) != NoErr
            || list == IntPtr.Zero)
            return result;

        int stride = Marshal.SizeOf<NativeEventInfo>();
        for (int i = 0; i < size; i++)
        {
            var e = Marshal.PtrToStructure<NativeEventInfo>(list + i * stride);
            var item = new EpgEventRow
            {
                Onid = svc.Onid, Tsid = svc.Tsid, Sid = svc.Sid,
                EventId     = e.event_id,
                StartTime   = e.StartTimeFlag != 0 ? e.start_time.ToDateTime() : null,
                DurationSec = e.DurationFlag  != 0 ? e.durationSec : null,
                FreeCaFlag  = e.freeCAFlag,
            };

            if (e.shortInfo != IntPtr.Zero)
            {
                var s = Marshal.PtrToStructure<NativeShortEvent>(e.shortInfo);
                item.EventName = Str(s.event_name, s.event_nameLength);
                item.ShortText = Str(s.text_char,  s.text_charLength);
            }
            if (e.extInfo != IntPtr.Zero)
            {
                var x = Marshal.PtrToStructure<NativeExtendedEvent>(e.extInfo);
                item.ExtText = Str(x.text_char, x.text_charLength);
            }
            if (e.componentInfo != IntPtr.Zero)
            {
                var c = Marshal.PtrToStructure<NativeComponentInfo>(e.componentInfo);
                item.ComponentStreamContent = c.stream_content;
                item.ComponentType          = c.component_type;
                item.ComponentTag           = c.component_tag;
                item.ComponentText          = Str(c.text_char, c.text_charLength);
            }
            if (e.contentInfo != IntPtr.Zero)
            {
                var ci = Marshal.PtrToStructure<NativeContentInfo>(e.contentInfo);
                if (ci.nibbleList != IntPtr.Zero)
                {
                    int gs = Marshal.SizeOf<NativeContent>();
                    for (int g = 0; g < ci.listSize; g++)
                    {
                        var nb = Marshal.PtrToStructure<NativeContent>(ci.nibbleList + g * gs);
                        item.Genres.Add(new EpgGenre
                        {
                            L1 = nb.content_nibble_level_1, L2 = nb.content_nibble_level_2,
                            User1 = nb.user_nibble_1,       User2 = nb.user_nibble_2,
                        });
                    }
                }
            }
            result.Add(item);
        }
        return result;
    }

    private static string Str(IntPtr p) =>
        p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p) ?? "";

    private static string Str(IntPtr p, int len) =>
        p == IntPtr.Zero || len <= 0 ? "" : Marshal.PtrToStringUni(p, len) ?? "";
}

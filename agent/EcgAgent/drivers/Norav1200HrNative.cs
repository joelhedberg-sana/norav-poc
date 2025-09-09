using System.Runtime.InteropServices;
using System.Text;

namespace EcgAgent.Drivers;

internal static class Norav1200HrNative
{
    private const string Dll = "NVECGUSB.dll";

    // NOTE: If calls fail immediately, switch CallingConvention to StdCall.
    private const CallingConvention CC = CallingConvention.Cdecl;

    [DllImport(Dll, CallingConvention = CC)] internal static extern uint Open();
    [DllImport(Dll, CallingConvention = CC)] internal static extern void Close();
    [DllImport(Dll, CallingConvention = CC)] internal static extern uint Start();
    [DllImport(Dll, CallingConvention = CC)] internal static extern uint Stop();

    // pMV_in_1_NUM returns device amplitude resolution (µV per 1 numeric unit)
    [DllImport(Dll, CallingConvention = CC)]
    internal static extern uint Init(int numOfChannels, int startLead, int smpRate,
                                     out double pMV_in_1_NUM, int mode /* =0 */);

    [DllImport(Dll, CallingConvention = CC)]
    internal static extern uint GetFwRev(byte[] rev /* len>=2 */);

    [DllImport(Dll, CallingConvention = CC)]
    internal static extern uint GetSerialNumber(out uint serNum);

    [DllImport(Dll, CallingConvention = CC)]
    internal static extern uint InitBNC(int bncOutputUSB, int trigWidthUSB /*=128*/, int volt /*=4*/, int worklead /*=0*/);

    [DllImport(Dll, CallingConvention = CC)]
    internal static extern uint SetBNCOut(int bncOutputUSB, int trigWidthUSB /*=128*/, int volt /*=4*/, int worklead /*=0*/);

    // Read 12-lead buffers from the 10s ring; returns non-zero if success (per doc)
    [DllImport(Dll, CallingConvention = CC)]
    internal static extern int ReadECGData(
        [In, Out] IntPtr[] ecgData,
        [In, Out] IntPtr[] ecgDataOrig,
        short buffsize,
        out int smpNum,
        IntPtr Count_InfoByte,
        IntPtr PwrButton, IntPtr BatLow, IntPtr BatVolt, IntPtr RF_Channel);

    [DllImport(Dll, CallingConvention = CC)]
    internal static extern int ReadECGDataFull(
        [In, Out] IntPtr[] ecgData,
        [In, Out] IntPtr[] ecgDataOrig,
        short buffsize,
        out int smpNum,
        IntPtr Count_InfoByte,
        IntPtr PwrButton, IntPtr BatLow, IntPtr BatVolt, IntPtr RF_Channel,
        IntPtr infoArray /* flags per sample: 0x01 trigger, 0x02 pacer */);

    // Optional textual decode; we can also map codes from the table in docs
    [DllImport(Dll, CallingConvention = CC, CharSet = CharSet.Ansi)]
    internal static extern void GetError(int status, StringBuilder str);
}

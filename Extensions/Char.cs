using DeviceLink.Const;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceLink.Extensions
{
    public static class Char
    {
        public static string ToPrintOutString(this char chr)
        {
            var byteChar = (byte)chr;
            return byteChar switch
            {
                ControlCode.NUL => "[NUL]",
                ControlCode.SOH => "[SOH]",
                ControlCode.STX => "[STX]",
                ControlCode.ETX => "[ETX]",
                ControlCode.EOT => "[EOT]",
                ControlCode.ENQ => "[ENQ]",
                ControlCode.ACK => "[ACK]",
                ControlCode.BEL => "[BEL]",
                ControlCode.BS_ => "[BS_]",
                ControlCode.HT_ => "[HT_]",
                ControlCode.LF_ => "[LF_]",
                ControlCode.VT_ => "[VT_]",
                ControlCode.FF_ => "[FF_]",
                ControlCode.CR_ => "[CR_]",
                ControlCode.SO_ => "[SO_]",
                ControlCode.SI_ => "[SI_]",
                ControlCode.DLE => "[DLE]",
                ControlCode.DC1 => "[DC1]",
                ControlCode.DC2 => "[DC2]",
                ControlCode.DC3 => "[DC3]",
                ControlCode.DC4 => "[DC4]",
                ControlCode.NAK => "[NAK]",
                ControlCode.SYN => "[SYN]",
                ControlCode.ETB => "[ETB]",
                ControlCode.CAN => "[CAN]",
                ControlCode.EM_ => "[EM_]",
                ControlCode.SU_ => "[SU_]",
                ControlCode.ESC => "[ESC]",
                ControlCode.FS_ => "[FS_]",
                ControlCode.GS_ => "[GS_]",
                ControlCode.RS_ => "[RS_]",
                ControlCode.US_ => "[US_]",
                _ => chr.ToString(),
            };
        }
    }
}

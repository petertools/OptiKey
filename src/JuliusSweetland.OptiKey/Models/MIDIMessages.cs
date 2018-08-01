using JuliusSweetland.OptiKey.Enums;
using JuliusSweetland.OptiKey.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JuliusSweetland.OptiKey.Models
{
    public static class MIDIMessages
    {
        public static readonly KeyValue Message01 = new KeyValue(FunctionKeys.MIDIMessage, "1");

        static MIDIMessages()
        {
        }

        public static bool IsMIDIMessage(string value)
        {
            Match matchCC = Regex.Match(value, @"Ch\. ([0-9]+) CC ([0-9]+)", RegexOptions.IgnoreCase);
            Match matchNote = Regex.Match(value, @"Ch\.([0-9]+) Note ([A-G][#]?)([0-9])", RegexOptions.IgnoreCase);

            return matchCC.Success || matchNote.Success;
        }

        public static bool IsCC(string value)
        {
            Match matchCC = Regex.Match(value, @"Ch\. ([0-9]+) CC ([0-9]+)", RegexOptions.IgnoreCase);

            return matchCC.Success;
        }

        public static bool IsNote(string value)
        {
            Match matchNote = Regex.Match(value, @"Ch\.([0-9]+) Note ([A-G][#]?)([0-9])", RegexOptions.IgnoreCase);

            return matchNote.Success;
        }

        public static Byte GetFirstByte(string value)
        {
            Match matchCC = Regex.Match(value, @"Ch\. ([0-9]+) CC ([0-9]+)", RegexOptions.IgnoreCase);
            Match matchNote = Regex.Match(value, @"Ch\.([0-9]+) Note ([A-G][#]?)([0-9])", RegexOptions.IgnoreCase);

            if (matchCC.Success)
                return Convert.ToByte(matchCC.Groups[2].Value);
            else if (matchNote.Success)
                return Convert.ToByte(matchNote.Groups[2].Value);
            else
                return 0;
        }

        internal static byte GetChannel(string value)
        {
            Match matchCC = Regex.Match(value, @"Ch\. ([0-9]+) CC ([0-9]+)", RegexOptions.IgnoreCase);
            Match matchNote = Regex.Match(value, @"Ch\.([0-9]+) Note ([A-G][#]?)([0-9])", RegexOptions.IgnoreCase);

            if (matchCC.Success)
                return Convert.ToByte(matchCC.Groups[1].Value);
            else if (matchNote.Success)
                return Convert.ToByte(matchNote.Groups[1].Value);
            else
                throw new ArgumentException();
        }
    }
}

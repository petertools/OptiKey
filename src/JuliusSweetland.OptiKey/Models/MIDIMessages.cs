using JuliusSweetland.OptiKey.Enums;
using JuliusSweetland.OptiKey.Properties;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JuliusSweetland.OptiKey.Models
{
    public static class MIDIMessages
    {
        public static readonly KeyValue Message01 = new KeyValue(FunctionKeys.MIDIMessage, "1");

        static MIDIMessages()
        {
        }
   }
}

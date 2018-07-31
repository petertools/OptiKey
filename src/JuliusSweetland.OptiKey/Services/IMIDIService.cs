using System;
using System.Collections.Generic;

namespace JuliusSweetland.OptiKey.Services
{
    public interface IMIDIService : INotifyErrors
    {
        List<string> GetAvailableInputDevices();
        List<string> GetAvailableOutputDevices();
        void SendMessage(Byte channel, Byte first, Byte second);
    }
}

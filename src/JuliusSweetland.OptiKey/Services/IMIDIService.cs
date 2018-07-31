using System;
using System.Collections.Generic;

namespace JuliusSweetland.OptiKey.Services
{
    public interface IMIDIService : INotifyErrors
    {
        List<string> GetAvailableInputDevices();
        List<string> GetAvailableOutputDevices();

        int SelectedInputDevice { get; set; }
        int SelectedOutputDevice { get; set; }

        void SendMessage(Byte channel, Byte first, Byte second);
    }
}

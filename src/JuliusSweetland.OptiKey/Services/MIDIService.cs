using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;
using System.Net;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using JuliusSweetland.OptiKey.Properties;
using JuliusSweetland.OptiKey.Services.Audio;
using log4net;
using Un4seen.Bass;
using NAudio.Midi;

namespace JuliusSweetland.OptiKey.Services
{
    public class MIDIService : IMIDIService
    {
        private int selectedInputDevice = -1;
        private MidiIn midiInputDevice = null;

        public int SelectedInputDevice
        {
            get
            {
                return selectedInputDevice;
            }

            set
            {
                if (selectedInputDevice != value)
                {
                    selectedInputDevice = value;
                    midiInputDevice = new MidiIn(selectedInputDevice);
                }
            }
        }

        private int selectedOutputDevice = -1;
        private MidiOut midiOutputDevice = null;

        public int SelectedOutputDevice
        {
            get
            {
                return selectedOutputDevice;
            }

            set
            {
                if (selectedOutputDevice != value)
                {
                    selectedOutputDevice = value;
                    midiOutputDevice = new MidiOut(selectedOutputDevice);
                }
            }
        }

        #region Events

        public event EventHandler<Exception> Error;

        #endregion

        #region Ctor

        public MIDIService()
        {
        }

        #endregion

        #region Public methods

        public List<string> GetAvailableInputDevices()
        {
            List<string> devices = new List<string>();

            for (int i = 0; i < MidiIn.NumberOfDevices; ++i)
                devices.Add(MidiIn.DeviceInfo(i).ProductName);

            return devices;
        }

        public List<string> GetAvailableOutputDevices()
        {
            List<string> devices = new List<string>();

            for (int i = 0; i < MidiOut.NumberOfDevices; ++i)
                devices.Add(MidiOut.DeviceInfo(i).ProductName);

            return devices;
        }

        public void SendMessage(Byte channel, Byte first, Byte second)
        {
            if (midiOutputDevice != null)
            {
                var noteOnEvent = new NoteOnEvent(0, channel, first, second, 50);
                midiOutputDevice.Send(noteOnEvent.GetAsShortMessage());
            }
        }

        private void PublishError(object sender, Exception ex)
        {
            //Log.Error("Publishing Error event (if there are any listeners)", ex);
            if (Error != null)
            {
                Error(sender, ex);
            }
        }

        #endregion
    }
}

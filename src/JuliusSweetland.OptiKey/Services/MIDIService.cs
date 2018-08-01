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
    public class MIDIMessageEvent
    {
        protected event EventHandler<Byte> eventDelegate;

        public void Dispatch(Byte value)
        {
            if (eventDelegate != null)
                eventDelegate(this, value);
        }

        public static MIDIMessageEvent operator +(MIDIMessageEvent Message, EventHandler<Byte> Delegate)
        {
            Message.eventDelegate += Delegate;
            return Message;
        }

        public static MIDIMessageEvent operator -(MIDIMessageEvent Message, EventHandler<Byte> Delegate)
        {
            Message.eventDelegate -= Delegate;
            return Message;
        }
    }

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
                    midiInputDevice.MessageReceived += midiIn_MessageReceived;
                    midiInputDevice.Start();
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

        public MIDIMessageEvent[] CC = new MIDIMessageEvent[128];
        public MIDIMessageEvent[] Note = new MIDIMessageEvent[128];

        #endregion

        #region Ctor

        public MIDIService()
        {
            for (int i = 0; i < 128; i++)
            {
                CC[i] = new MIDIMessageEvent();
                Note[i] = new MIDIMessageEvent();
            }
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
                var onEvent = new ControlChangeEvent(0, channel, (MidiController) first, second);
                midiOutputDevice.Send(onEvent.GetAsShortMessage());
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

        void midiIn_MessageReceived(object sender, MidiInMessageEventArgs e)
        {
            Console.WriteLine(String.Format("Time {0} Message 0x{1:X8} Event {2}", e.Timestamp, e.RawMessage, e.MidiEvent));

            Byte first = Convert.ToByte((e.RawMessage >> 8) & 0xFF);
            Byte second = Convert.ToByte((e.RawMessage >> 16) & 0xFF);

            switch (e.MidiEvent.CommandCode)
            {
                case MidiCommandCode.ControlChange:
                    CC[first].Dispatch(second);
                    break;

                case MidiCommandCode.NoteOn:
                    Note[first].Dispatch(second);
                    break;

                case MidiCommandCode.NoteOff:
                    Note[first].Dispatch(0);
                    break;

                default:
                    break;
            }
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using JuliusSweetland.OptiKey.Extensions;
using JuliusSweetland.OptiKey.Properties;
using JuliusSweetland.OptiKey.Services;
using log4net;
using Prism.Commands;
using Prism.Mvvm;
using System.IO;

namespace JuliusSweetland.OptiKey.UI.ViewModels.Management
{
    public class MIDIViewModel : BindableBase
    {
        private IMIDIService midiService;
        
        #region Ctor

        public MIDIViewModel(IMIDIService midiService)
        {
            this.midiService = midiService;
            
            Load();
        }
        
        #endregion
        
        #region Properties

        public List<string> InputDevices
        {
            get { return midiService.GetAvailableInputDevices(); }
        }

        public List<string> OutputDevices
        {
            get { return midiService.GetAvailableOutputDevices(); }
        }

        private int inputDeviceIndex;
        public int InputDeviceIndex
        {
            get { return inputDeviceIndex; }
            set { SetProperty(ref inputDeviceIndex, value); }
        }

        private int outputDeviceIndex;
        public int OutputDeviceIndex
        {
            get { return outputDeviceIndex; }
            set { SetProperty(ref outputDeviceIndex, value); }
        }
        
        #endregion
        
        #region Methods

        private void Load()
        {
            inputDeviceIndex = midiService.SelectedInputDevice;
            outputDeviceIndex = midiService.SelectedOutputDevice;
        }

        public void ApplyChanges()
        {
            midiService.SelectedInputDevice = inputDeviceIndex;
            midiService.SelectedOutputDevice = outputDeviceIndex;
        }

        #endregion
    }
}

using System.Windows;
using JuliusSweetland.OptiKey.Properties;
using JuliusSweetland.OptiKey.Services;
using JuliusSweetland.OptiKey.UI.ViewModels;
using MahApps.Metro.Controls;

namespace JuliusSweetland.OptiKey.UI.Windows
{
    /// <summary>
    /// Interaction logic for ManagementWindow.xaml
    /// </summary>
    public partial class ManagementWindow : MetroWindow
    {
        public ManagementWindow(
            IMIDIService midiService,
            IAudioService audioService,
            IDictionaryService dictionaryService)
        {
            InitializeComponent();

            //Instantiate ManagementViewModel and set as DataContext of ManagementView
            var managementViewModel = new ManagementViewModel(midiService, audioService, dictionaryService);
            this.ManagementView.DataContext = managementViewModel;
        }
    }
}

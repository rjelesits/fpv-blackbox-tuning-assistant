using System.Windows;

namespace FPVBlackboxTuningAssistant;

public partial class DroneSelectionWindow : Window
{
    public DroneSelectionWindow(
        IEnumerable<string> existingDroneNames,
        string suggestedLogName,
        string detectedFlightControllerModel,
        string selectedDroneName = "",
        string? selectedLogName = null)
    {
        InitializeComponent();
        DroneNameComboBox.ItemsSource = existingDroneNames.ToArray();
        FlightControllerModelText.Text = string.IsNullOrWhiteSpace(detectedFlightControllerModel)
            ? "Im Log nicht angegeben"
            : detectedFlightControllerModel;
        DroneNameComboBox.Text = string.IsNullOrWhiteSpace(selectedDroneName)
            ? detectedFlightControllerModel
            : selectedDroneName;
        LogNameTextBox.Text = string.IsNullOrWhiteSpace(selectedLogName) ? suggestedLogName : selectedLogName;
        Loaded += (_, _) => LogNameTextBox.Focus();
    }

    public string SelectedDroneName { get; private set; } = string.Empty;
    public string SelectedLogName { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = DroneNameComboBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(
                this,
                "Wähle eine Drohne aus oder gib einen Namen ein.",
                "Drohne erforderlich",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DroneNameComboBox.Focus();
            return;
        }

        var logName = LogNameTextBox.Text.Trim();
        if (logName.Length == 0)
        {
            MessageBox.Show(
                this,
                "Gib einen Dateinamen für den Log und die Auswertung ein.",
                "Dateiname erforderlich",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            LogNameTextBox.Focus();
            return;
        }

        SelectedDroneName = name;
        SelectedLogName = logName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

// =============================================================================
// InputDialog.xaml.cs
//
// Code-behind for the Grid Tube Generator input dialog (InputDialog.xaml).
// Collects the user inputs (target ROI, shape, diameter, gap, boundary
// margin) and validates them before returning control to
// GridTubeGenerator.cs.
// =============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace VMS.TPS
{
    public partial class InputDialog : Window
    {
        public string SelectedRoi { get; private set; }
        public GridShape SelectedShape { get; private set; }
        public double DiameterMm { get; private set; }
        public double GapMm { get; private set; }
        public double BoundaryMarginMm { get; private set; }

        public InputDialog(List<string> roiNames)
        {
            InitializeComponent();

            foreach (string roiName in roiNames)
            {
                RoiComboBox.Items.Add(roiName);
            }

            if (RoiComboBox.Items.Count > 0)
            {
                RoiComboBox.SelectedIndex = 0;
            }
        }

        private void OnGenerateClicked(object sender, RoutedEventArgs e)
        {
            if (RoiComboBox.SelectedItem == null)
            {
                MessageBox.Show(this, "Select a target ROI.", "Grid Tube Generator",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double diameter, gap, boundaryMargin;

            if (!double.TryParse(DiameterTextBox.Text, out diameter) || diameter <= 0)
            {
                MessageBox.Show(this, "Enter a positive diameter (mm).", "Grid Tube Generator",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(GapTextBox.Text, out gap) || gap <= 0)
            {
                MessageBox.Show(this, "Enter a positive tube/sphere gap (mm).", "Grid Tube Generator",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(BoundaryMarginTextBox.Text, out boundaryMargin) || boundaryMargin < 0)
            {
                MessageBox.Show(this, "Enter a non-negative boundary margin (mm).", "Grid Tube Generator",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedRoi = RoiComboBox.SelectedItem.ToString();
            SelectedShape = (SphereRadioButton.IsChecked == true) ? GridShape.Sphere : GridShape.Tube;
            DiameterMm = diameter;
            GapMm = gap;
            BoundaryMarginMm = boundaryMargin;

            DialogResult = true;
            Close();
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

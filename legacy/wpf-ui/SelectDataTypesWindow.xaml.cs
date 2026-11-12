using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.Forms.MessageBox;

namespace netDigger
{
    public partial class SelectDataTypesWindow : Window
    {
        public List<string> SelectedDataTypes { get; private set; }
        public string SelectedPath { get; private set; }

        public SelectDataTypesWindow()
        {
            InitializeComponent();
            SelectedDataTypes = new List<string>();
            selectAllCheckBox.Checked += SelectAllCheckBox_Checked;
            selectAllCheckBox.Unchecked += SelectAllCheckBox_Unchecked;
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxes(true);
        }

        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxes(false);
        }

        private void SetAllCheckBoxes(bool isChecked)
        {
            foreach (var child in ((StackPanel)selectAllCheckBox.Parent).Children)
            {
                if (child is CheckBox checkBox && checkBox != selectAllCheckBox)
                {
                    checkBox.IsChecked = isChecked;
                }
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
               
            };

            System.Windows.Forms.DialogResult result = dialog.ShowDialog();

            if (result == System.Windows.Forms.DialogResult.OK)
            {
                pathTextBox.Text = dialog.SelectedPath;
                SelectedPath = dialog.SelectedPath;
            }
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedDataTypes.Clear();

            if (selectAllCheckBox.IsChecked == true)
            {
                SelectedDataTypes.AddRange(new[]
                {
                    "PDF", "CSV", "DOCX", "XLS", "PPTX", "TXT",
                    "Images", "Videos", "JSON", "DBSQL", "XML",
                    "HTML", "PHP", "JS", "Archives", "Miscellaneous"
                });
            }
            else
            {
                if (pdfCheckBox.IsChecked == true) SelectedDataTypes.Add("PDF");
                if (csvCheckBox.IsChecked == true) SelectedDataTypes.Add("CSV");
                if (docxCheckBox.IsChecked == true) SelectedDataTypes.Add("DOCX");
                if (xlsCheckBox.IsChecked == true) SelectedDataTypes.Add("XLS");
                if (pptxCheckBox.IsChecked == true) SelectedDataTypes.Add("PPTX");
                if (txtCheckBox.IsChecked == true) SelectedDataTypes.Add("TXT");
                if (imagesCheckBox.IsChecked == true) SelectedDataTypes.Add("Images");
                if (videosCheckBox.IsChecked == true) SelectedDataTypes.Add("Videos");
                if (jsonCheckBox.IsChecked == true) SelectedDataTypes.Add("JSON");
                if (dbsqlCheckBox.IsChecked == true) SelectedDataTypes.Add("DBSQL");
                if (xmlCheckBox.IsChecked == true) SelectedDataTypes.Add("XML");
                if (htmlCheckBox.IsChecked == true) SelectedDataTypes.Add("HTML");
                if (phpCheckBox.IsChecked == true) SelectedDataTypes.Add("PHP");
                if (jsCheckBox.IsChecked == true) SelectedDataTypes.Add("JS");
                if (archivesCheckBox.IsChecked == true) SelectedDataTypes.Add("Archives");
                if (miscCheckBox.IsChecked == true) SelectedDataTypes.Add("Miscellaneous");
            }

            if (SelectedDataTypes.Count == 0)
            {
                MessageBox.Show("Please select at least one data type.", "netDigger");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedPath))
            {
                MessageBox.Show("Please select a path.", "netDigger");
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}

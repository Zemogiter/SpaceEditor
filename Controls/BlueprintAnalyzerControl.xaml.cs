using Microsoft.Win32;
using SpaceEditor.Algorithms;
using SpaceEditor.Data;
using System.Windows;
using System.Windows.Controls;

namespace SpaceEditor.Controls;

public partial class BlueprintAnalyzerControl : UserControl
{
    // A static or injected reference to GameProxy is needed. 
    // Assuming MainWindow or your app setup makes GameProxy globally accessible, 
    // or you pass it into this control after it initializes.
    public GameProxy Proxy { get; set; }

    public BlueprintAnalyzerControl()
    {
        InitializeComponent();
    }

    private async void BtnSelectBlueprint_Click(object sender, RoutedEventArgs e)
    {
        if (this.Proxy == null)
        {
            MessageBox.Show("Game data is still loading or GameProxy is not assigned.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var openFileDialog = new OpenFileDialog
        {
            Title = "Select Blueprint",
            Filter = "VRAGE3 Blueprints (*.vrb)|*.vrb|All files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            // UI Update: Show loading state
            BtnSelectBlueprint.IsEnabled = false;
            TxtStatus.Text = "Analyzing Blueprint... (This might take a moment)";
            //TxtStatus.Foreground = System.Windows.Media.Brushes.Yellow;
            TxtStatus.Visibility = Visibility.Visible;
            DgComponents.ItemsSource = null;

            try
            {
                var analyzer = new BlueprintAnalyzerService(this.Proxy);

                // Run the heavy reflection/parsing on a background thread so the UI doesn't hang
                var result = await System.Threading.Tasks.Task.Run(() => analyzer.AnalyzeAsync(openFileDialog.FileName));

                // Convert the raw dictionary into our bindable list and sort by amount descending
                var componentList = result.TotalComponents
                    .Select(kvp => new ComponentCost { ComponentName = kvp.Key, Amount = kvp.Value })
                    .OrderByDescending(c => c.Amount)
                    .ToList();

                // UI Update: Display results safely back on the main thread
                TxtBlueprintName.Text = result.BlueprintName;
                TxtTotalBlocks.Text = result.TotalBlocks.ToString("N0");
                DgComponents.ItemsSource = componentList;

                TxtStatus.Text = $"Analysis complete! Found {componentList.Count} unique components.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;

                // Optional: Alert the developer if there are blocks we don't have recipes for
                if (result.UnknownBlocks.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"[Analyzer] Missing definitions for: {string.Join(", ", result.UnknownBlocks)}");
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Analysis failed.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                MessageBox.Show($"Failed to analyze blueprint:\n{ex.Message}", "Analysis Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSelectBlueprint.IsEnabled = true;
            }
        }
    }
}
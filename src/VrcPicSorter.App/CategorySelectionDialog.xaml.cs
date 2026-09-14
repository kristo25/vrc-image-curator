using System.Windows;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.App;

public partial class CategorySelectionDialog : Window
{
    public CategorySelectionDialog()
    {
        InitializeComponent();
        CategoryCombo.ItemsSource = AppStateDefaults.FixedCategories;
        CategoryCombo.SelectedIndex = 0;
    }

    public VrcImageCategory SelectedCategory => (VrcImageCategory)CategoryCombo.SelectedItem;

    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
}

using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace VoxCore.Client;

public sealed partial class CrewManifest : UserControl
{
    public CrewManifest()
    {
        InitializeComponent();
    }

    public void BindMembers(ObservableCollection<MemberItem> members)
    {
        MembersListView.ItemsSource = members;
    }
}

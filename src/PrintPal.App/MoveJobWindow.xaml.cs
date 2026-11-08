using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

public partial class MoveJobWindow : Window
{
    public MoveJobWindow(string documentName, IReadOnlyList<BillSummary> bills, Guid? currentBillId)
    {
        InitializeComponent();

        DocumentText.Text = $"把「{documentName}」记到另一张账单";

        var choices = bills
            .Where(bill => bill.Status != BillStatus.Closed)
            .Select(bill => new MoveJobChoice(
                bill.Id,
                bill.Name,
                $"{Money.FormatCents(bill.AmountCents)} · {bill.JobCount} 单",
                bill.Id == currentBillId))
            .ToList();

        choices.Add(new MoveJobChoice(null, "＋ 新建账单（并切换为当前）", "为这位客户新开一张账单", false));

        ChoiceList.ItemsSource = choices;
        ChoiceList.SelectedItem = choices.First(choice => !choice.IsCurrent);
    }

    public Guid? TargetBillId => (ChoiceList.SelectedItem as MoveJobChoice)?.BillId;

    public bool CreateNew => ChoiceList.SelectedItem is MoveJobChoice { IsNew: true };

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ChoiceList.SelectedItem is not MoveJobChoice choice || choice.IsCurrent)
        {
            MessageBox.Show("请选择目标账单。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}

public sealed class MoveJobChoice
{
    public MoveJobChoice(Guid? billId, string name, string meta, bool isCurrent)
    {
        BillId = billId;
        Name = name;
        Meta = meta;
        IsCurrent = isCurrent;
    }

    public Guid? BillId { get; }

    public string Name { get; }

    public string Meta { get; }

    public bool IsCurrent { get; }

    public bool IsNew => BillId is null;
}

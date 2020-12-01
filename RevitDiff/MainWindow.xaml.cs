using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.IO;
using System;

namespace RvtDiff
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(RvtDiff rdc)
        {
            InitializeComponent();
            this.rdc = rdc;
            trVAllVM = new TreeViewlVM();
            DataContext = trVAllVM;
            btnApply.IsEnabled = false;
            cbAlgorithm.SelectedIndex = 3;
        }
        public RvtDiff rdc;
        public TreeViewlVM trVAllVM;
        public bool isMatchingFirst { get { return cbAlgorithm.SelectedIndex == 0; } }

        public void setDocPath(string currPath, string oldPath = "")
        {
            if (oldPath == "")
                oldPath = currPath;

            int indM = oldPath.LastIndexOf('\\') + 1, n;
            if (oldPath[indM] == 'M' && int.TryParse(oldPath[indM + 1] + "", out n))
                oldPath = oldPath.Substring(0, indM + 1) + n.ToString() + ".rvt";

            textBox1.Text = oldPath;
            textBox2.Text = currPath;

            btnStart.Focus();
        }

        private void btn1_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "RVT文件|*.rvt";
            if (dialog.ShowDialog() == true)
                textBox1.Text = dialog.FileName;

        }

        private void btn2_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "RVT文件|*.rvt";
            if (dialog.ShowDialog() == true)
                textBox2.Text = dialog.FileName;
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            string path1 = textBox1.Text.Trim();
            string path2 = textBox2.Text.Trim();

            rdc.CompareRvtDoc(path1, path2);
            btnApply.IsEnabled = true;
        }

        private void btnTest_Click(object sender, RoutedEventArgs e)
        {
            List<List<double>> costTimes = new List<List<double>>();
            costTimes.Add(new List<double>()); costTimes.Add(new List<double>());
            costTimes.Add(new List<double>()); costTimes.Add(new List<double>());

            string folderPath = textBox1.Text.Trim(); //endswith "\Mx.rvt"
            folderPath = folderPath.Substring(0, folderPath.LastIndexOf('\\') + 1);
            int indM = folderPath.Substring(0, folderPath.Length - 1).LastIndexOf('\\') + 1;
            string Mx = folderPath.Substring(indM, 2);
            textBox1.Text = folderPath; textBox2.Text = Mx + " - TEST";

            DirectoryInfo folder = new DirectoryInfo(folderPath);
            foreach (FileInfo file in folder.GetFiles("*.rvt"))
                for (int i = 0; i < 4; i++)
                {
                    cbAlgorithm.SelectedIndex = i;
                    costTimes[i].Add(rdc.CompareRvtDoc(folderPath + Mx + ".rvt", file.FullName));
                }

            List<double> avgCostTimes = new List<double>();
            for (int i = 0; i < 4; i++)
                avgCostTimes.Add(costTimes[i].Average());

            Clipboard.SetDataObject(string.Join("\t", avgCostTimes));
            MessageBox.Show(string.Join(",", avgCostTimes));
        }

        private void lvAll_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LvElement lvE = lVAll.SelectedItem as LvElement;
            if (lvE == null)
                return;

            if (lvE.ChangeType == "*")
                tbAll.Text = "[Modified Element]";
            else if (lvE.ChangeType == "=")
                tbAll.Text = "[Unchanged Element]";
            else if (lvE.ChangeType == "+")
                tbAll.Text = "[Added Element]";
            else if (lvE.ChangeType == "-")
                tbAll.Text = "[Deleted Element]";

            tbAll.Text += "\n" + rdc.getEIdInfo(lvE.EId, lvE.ChangeType, true, "\n");
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            rdc.refreshLVByTrV();
            setTrVAllAbstract();
        }

        private void highlightSelectedItem(object sender, RoutedEventArgs e)
        {
            var lvEs = lVAll.SelectedItems;
            if (lvEs == null || lvEs.Count == 0)
                return;

            List<int> eIds = new List<int>();
            foreach (LvElement lve in lvEs)
                eIds.Add(lve.EId);

            rdc.highlightEle(eIds);
        }

        //In the left bottom of the window
        public void setTrVAllAbstract()
        {
            string info = "";

            TrVElement root = trVAllVM.trVAllItems[0];
            if (root.IsChecked == true)
            {
                info += root.Children.Count + " categories are selected.\n\n";
                info += getAbstracByTrVEle(root);
            }
            else if (root.IsChecked == null)
            {
                TrVElement tmp = new TrVElement { AllCount = 0, ModifyCount = 0, DelCount = 0, AddCount = 0 };
                int selectedTrVECount = 0;
                foreach (TrVElement t in root.Children)
                {
                    if (t.IsChecked == true)
                    {
                        tmp.AllCount += t.AllCount;
                        tmp.ModifyCount += t.ModifyCount;
                        tmp.DelCount += t.DelCount;
                        tmp.AddCount += t.AddCount;
                        selectedTrVECount++;
                    }
                }
                info += selectedTrVECount + " categories are selected.\n\n";
                info += getAbstracByTrVEle(tmp);
            }
            else
            {
                info += "0 categories are selected.\n\n";
            }

            tbAbstract.Text = info;
        }

        private string getAbstracByTrVEle(TrVElement t)
        {
            int cc = t.ModifyCount + t.DelCount + t.AddCount;
            return t.AllCount + " elements in selected categories\n" + cc + " elements are changed\n(" + t.ModifyCount + " modified/ "
                + t.DelCount + " deleted/ " + t.AddCount + " added)";
        }

    }

    public class LvElement
    {
        public int Number { get; set; }
        public int EId { get; set; }
        public string Name { get; set; }
        public string ChangeType { get; set; }
        public string Category { get; set; }
        public int ChangeTypeNum { get; set; }
        public string ToolTip { get { return getToolTip(); } }

        public string getToolTip()
        {
            if (ChangeType == "*")
                return "Modified element";
            else if (ChangeType == "=")
                return "Unchanged element";
            else if (ChangeType == "+")
                return "Added element (exists in the new file)";
            else if (ChangeType == "-")
                return "Deleted element (exists in the old file)";

            return "";
        }

        public static int getChangeTypeNum(string ChangeType)
        {
            if (ChangeType == "=")
                return 10;

            if (ChangeType == "*")
                return 1;
            if (ChangeType == "-")
                return 2;
            if (ChangeType == "+")
                return 3;

            return -1;
        }

    }

}

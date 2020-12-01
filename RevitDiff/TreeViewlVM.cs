using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RvtDiff
{
    public class TreeViewlVM : INotifyPropertyChanged
    {
        public TreeViewlVM()
        {
            InitTrVAll();
        }
        public event PropertyChangedEventHandler PropertyChanged;

        private ObservableCollection<TrVElement> _trVAllItems;
        public ObservableCollection<TrVElement> trVAllItems
        {
            get { return _trVAllItems; }
            set { _trVAllItems = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("trVAllItems")); }
        }


        private void InitTrVAll()
        {
            trVAllItems = new ObservableCollection<TrVElement> { new TrVElement { CategoryId = 1, Header = "All", IsChecked = true } };
        }

    }

    public class TrVElement : INotifyPropertyChanged
    {
        public int CategoryId { get; set; }
        public string Header { get; set; }
        public TrVElement Parent { get; set; }

        public int AllCount { get; set; } //=UnchangedCount+ModifyCount+DelCount+AddCount
        public int ModifyCount { get; set; }
        public int DelCount { get; set; }
        public int AddCount { get; set; }

        private bool? _IsChecked = false;
        private List<TrVElement> _Children = null;
        public bool? IsChecked
        {
            get { return _IsChecked; }
            set { SetIsChecked(value, true, true); }
        }
        public List<TrVElement> Children
        {
            get { return _Children; }
            set { _Children = value; SetParentValue(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged(string propertyname)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyname));
        }

        // 设置节点IsChecked的值
        private void SetIsChecked(bool? value, bool isUpdateChildren, bool isUpdateParent)
        {
            _IsChecked = value;

            if (isUpdateChildren && _IsChecked.HasValue && Children != null)
                Children.ForEach(n => n.SetIsChecked(_IsChecked, true, false));

            if (isUpdateParent && Parent != null)
                Parent.VerifyCheckState();

            RaisePropertyChanged("IsChecked");
        }

        // 验证并设置父级节点的IsChecked的值
        private void VerifyCheckState()
        {
            bool? state = null;
            for (int i = 0; i < Children.Count; ++i)
            {
                bool? current = Children[i]._IsChecked;
                if (i == 0)
                {
                    state = current;
                }
                else if (state != current)
                {
                    state = null;
                    break;
                }
            }
            SetIsChecked(state, false, true);
        }

        // 数据初始化时设置父节点的值
        private void SetParentValue()
        {
            if (Children != null)
            {
                Children.ForEach(n => n.Parent = this);
            }
        }

    }

}

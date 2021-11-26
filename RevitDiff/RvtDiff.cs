using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace RvtDiff
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RvtDiff : IExternalCommand
    {
        private MainWindow MW;
        private UIApplication UIApp;
        private Document Doc1, Doc2;
        private List<Element> Eles1, Eles2;
        private List<int> EIds1, EIds2;
        private List<int> CategoryIds1, CategoryIds2;
        private List<int> UnchangedEIds, ModifyEIds, AddEIds, DelEIds, AllEIds;
        private Dictionary<int, Element> EIdEles1, EIdEles2;
        private Dictionary<int, double> EIdHashCodes1, EIdHashCodes2;
        private Dictionary<int, int> EIdChangesFrom;//for same element <eId2,eId1>

        private static int CategoryIdNullHashCode = "CategoryIdNull".GetHashCode();
        private static int CategoryHashCode = "Category".GetHashCode(), GeometryHashCode = "Geometry".GetHashCode();
        private static int LocationHashCode = "Location".GetHashCode(), ParameterHashCode = "Parameter".GetHashCode();

        public Result Execute(ExternalCommandData cmdData, ref string msg, ElementSet elements)
        {
            UIApp = cmdData.Application;

            MW = new MainWindow(this);
            MW.setDocPath(UIApp.ActiveUIDocument.Document.PathName);
            MW.Show();

            return Result.Succeeded;
        }

        //return the execution time
        public double diffRvtDoc(string path1, string path2)
        {
            //if (!(path1.EndsWith(".rvt") && path2.EndsWith(".rvt")))
            //{ MessageBox.Show("Need \".rvt\" file"); return; }

            if (path1 == path2)
            {
                if (MessageBox.Show("Please input two different rvt paths.\r\n(Start testing dir?)", "", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                { _testAllDirFiles(path1); return 0.0; }
                else
                    return 0.0;
            }

            printStatus("Loading Document...");
            Doc1 = UIApp.Application.OpenDocumentFile(path1);
            Doc2 = UIApp.Application.OpenDocumentFile(path2);

            Stopwatch stopwatch1 = new Stopwatch();
            stopwatch1.Restart();
            printStatus("Initializing...");
            initEleIdHashCodes();

            printStatus("Comparing Elements...");
            if (MW.isMatchingFirst)//Matching-first
                classifyChanges_M(out UnchangedEIds, out ModifyEIds, out AddEIds, out DelEIds, out AllEIds);
            else if (MW.cbAlgorithm.SelectedIndex == 1)//Comparison-first
                classifyChanges_C(out UnchangedEIds, out ModifyEIds, out AddEIds, out DelEIds, out AllEIds);
            else if (MW.cbAlgorithm.SelectedIndex == 2)//Hash code-based comparison-first
                classifyChanges_CHc(out UnchangedEIds, out ModifyEIds, out AddEIds, out DelEIds, out AllEIds);
            else if (MW.cbAlgorithm.SelectedIndex == 3)//Quick hash code-based comparison-first
                classifyChanges_CHc_quick(out UnchangedEIds, out ModifyEIds, out AddEIds, out DelEIds, out AllEIds);

            printStatus("Setting Result Display...");
            setLVAllByEIds(ref AllEIds);
            initTrVAll();

            stopwatch1.Stop(); double costTime = stopwatch1.ElapsedMilliseconds / 1000.0;
            printStatus("Time Cost: " + costTime.ToString("f2") + "s");
            //hashCollisionCheck();

            MW.tbAll.Clear();
            return costTime;
        }

        void initEleIdHashCodes()
        {
            FilteredElementCollector collector1 = new FilteredElementCollector(Doc1), collector2 = new FilteredElementCollector(Doc2);
            collector1.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(true), new ElementIsElementTypeFilter(true)));
            collector2.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(true), new ElementIsElementTypeFilter(true)));

            Eles1 = collector1.Where(e => !(e is ElementType) && e.Category != null && e.LevelId != null && e.get_Geometry(new Options()) != null).OrderBy(e => e.Id.IntegerValue).ToList();
            Eles2 = collector2.Where(e => !(e is ElementType) && e.Category != null && e.LevelId != null && e.get_Geometry(new Options()) != null).OrderBy(e => e.Id.IntegerValue).ToList();
            //Eles2 = collector2.OrderBy(e => Math.Pow(e.Id.IntegerValue, 2) % 100).ToList(); //random cases, need disable acceleration
            //Eles2 = collector2.OrderByDescending(e => e.Id.IntegerValue).ToList();

            EIds1 = Eles1.Select(e => e.Id.IntegerValue).ToList();
            EIds2 = Eles2.Select(e => e.Id.IntegerValue).ToList();

            CategoryIds1 = Eles1.Select(e => e?.Category?.Id.IntegerValue ?? CategoryIdNullHashCode).ToList();
            CategoryIds2 = Eles2.Select(e => e?.Category?.Id.IntegerValue ?? CategoryIdNullHashCode).ToList();

            EIdEles1 = Eles1.ToDictionary(e => e.Id.IntegerValue, e => e);
            EIdEles2 = Eles2.ToDictionary(e => e.Id.IntegerValue, e => e);

            EIdHashCodes1 = new Dictionary<int, double>();
            EIdHashCodes2 = new Dictionary<int, double>();
            if (MW.cbAlgorithm.SelectedIndex <= 1)
            {
                foreach (var elem in Eles1)
                    EIdHashCodes1.Add(elem.Id.IntegerValue, 0);
                foreach (var elem in Eles2)
                    EIdHashCodes2.Add(elem.Id.IntegerValue, 0);
            }
            else //use HashCode
            {
                printStatus("Calculating HashCode...");
                for (int i = 0; i < Eles1.Count; i++)
                    getEleHashCode(Eles1[i], ref EIdHashCodes1);
                for (int i = 0; i < Eles2.Count; i++)
                    getEleHashCode(Eles2[i], ref EIdHashCodes2);

                //ordered by key, which do not exist in EidEles should be deleted.
                EIdHashCodes1 = EIdHashCodes1.OrderBy(kv => kv.Key).Where(kv => EIdEles1.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                EIdHashCodes2 = EIdHashCodes2.OrderBy(kv => kv.Key).Where(kv => EIdEles2.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }

        void hashCollisionCheck()
        {
            if (MW.cbAlgorithm.SelectedIndex <= 1)
                return;

            int hashCollisionCount = 0;
            foreach (var eleHc1 in EIdHashCodes1)
            {
                var eId2s = EIdHashCodes2.Where(idhc => idhc.Value == eleHc1.Value).Select(idhc => idhc.Key).ToList();
                hashCollisionCount += eId2s.Count - 1;

                //foreach (var eleHc2 in EIdHashCodes2)
                //{
                //    if (eleHc1.Value == eleHc2.Value && !eleEqual(EIdEles1[eleHc1.Key], EIdEles2[eleHc2.Key]))
                //        hashCollisionCount++;
                //}
            }
            double hashClooisionRate = (hashCollisionCount + 0.0) / (Eles1.Count + Eles2.Count) * 2;
            printStatus("hashCollision=" + hashCollisionCount + ", rate=" + hashClooisionRate.ToString("f2"), true);
        }

        void classifyChanges_M(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();

            //List<Element> Eles1(2): all elements in File A(B), ordered by Id.

            int i = 0, j = 0, eId1i, eId2j;
            while (i < Eles1.Count && j < Eles2.Count)
            {
                eId1i = Eles1[i].Id.IntegerValue; eId2j = Eles2[j].Id.IntegerValue;
                if (eId1i < eId2j)
                { delEIds.Add(eId1i); i++; }
                else if (eId1i == eId2j)
                {
                    if (eleEqual(Eles1[i], Eles2[j])) unchangedEIds.Add(eId2j);
                    else modifyEIds.Add(eId2j);
                    i++; j++;
                }
                else //if(eId1 > eId2)
                { addEIds.Add(eId2j); j++; }
            }
            while (i < Eles1.Count)
            { delEIds.Add(Eles1[i].Id.IntegerValue); i++; }
            while (j < Eles2.Count)
            { addEIds.Add(Eles2[j].Id.IntegerValue); j++; }

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }

        void classifyChanges_C(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();

            Dictionary<int, Element> eIdEles1_ = new Dictionary<int, Element>(EIdEles1);
            Dictionary<int, Element> eIdEles2_ = new Dictionary<int, Element>(EIdEles2);

            //*
            int eId;
            foreach (var ee1 in EIdEles1)//reduce minor error, and accelerate (best case)
            {
                eId = ee1.Key;
                if (EIdEles2.ContainsKey(eId) && eleEqual(ee1.Value, EIdEles2[eId]))
                {
                    unchangedEIds.Add(eId);
                    eIdEles1_.Remove(eId); eIdEles2_.Remove(eId);
                }
            }//*/

            Dictionary<int, Element> _eIdEles1_ = new Dictionary<int, Element>(eIdEles1_);
            foreach (var ee1 in _eIdEles1_)
            {
                foreach (var ee2 in eIdEles2_)
                {
                    if (eleEqual(ee1.Value, ee2.Value))
                    {
                        unchangedEIds.Add(ee2.Key);
                        eIdEles1_.Remove(ee1.Key); eIdEles2_.Remove(ee2.Key);
                        if (ee1.Key != ee2.Key) EIdChangesFrom.Add(ee2.Key, ee1.Key);
                        break;
                    }
                }
            }
            foreach (var ee1 in eIdEles1_)
            {
                if (eIdEles2_.ContainsKey(ee1.Key))
                {
                    modifyEIds.Add(ee1.Key);
                    eIdEles2_.Remove(ee1.Key);
                }
                else
                    delEIds.Add(ee1.Key);
            }
            foreach (var ee2 in eIdEles2_)
                addEIds.Add(ee2.Key);

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }

        void classifyChanges_CHc(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();
            //EleHashCodes1/2 have been initialized
            Dictionary<int, double> eIdHashCodes1_ = new Dictionary<int, double>(EIdHashCodes1); //for Remove()
            Dictionary<int, double> eIdHashCodes2_ = new Dictionary<int, double>(EIdHashCodes2);

            //*
            foreach (var eIdHc1 in EIdHashCodes1) //reduce minor error, and accelerate
            {
                if (EIdHashCodes2.ContainsKey(eIdHc1.Key) && eIdHc1.Value == EIdHashCodes2[eIdHc1.Key]
                    && eleEqual(EIdEles1[eIdHc1.Key], EIdEles2[eIdHc1.Key]))
                {
                    unchangedEIds.Add(eIdHc1.Key);
                    eIdHashCodes1_.Remove(eIdHc1.Key); eIdHashCodes2_.Remove(eIdHc1.Key);
                }
            } //*/

            Dictionary<int, double> _eIdHashCodes1_ = new Dictionary<int, double>(eIdHashCodes1_);
            foreach (var eIdHc1 in _eIdHashCodes1_)
            {
                foreach (var eIdHc2 in eIdHashCodes2_)
                {
                    if (eIdHc1.Value == eIdHc2.Value && eleEqual(EIdEles1[eIdHc1.Key], EIdEles2[eIdHc2.Key]))
                    {
                        unchangedEIds.Add(eIdHc2.Key);
                        eIdHashCodes1_.Remove(eIdHc1.Key); eIdHashCodes2_.Remove(eIdHc2.Key);
                        if (eIdHc1.Key != eIdHc2.Key) EIdChangesFrom.Add(eIdHc2.Key, eIdHc1.Key);
                        break;
                    }
                }
            }

            foreach (var eIdHc1 in eIdHashCodes1_)
            {
                if (eIdHashCodes2_.ContainsKey(eIdHc1.Key))
                { modifyEIds.Add(eIdHc1.Key); eIdHashCodes2_.Remove(eIdHc1.Key); }
                else
                    delEIds.Add(eIdHc1.Key);
            }
            addEIds = eIdHashCodes2_.Select(idhc => idhc.Key).ToList();

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }

        void classifyChanges_CHc_quick(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            //quick: assume no hash collision will happen

            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();
            //EleHashCodes1/2 have been initialized
            Dictionary<int, double> eIdHashCodes1_ = new Dictionary<int, double>(EIdHashCodes1); //for Remove()
            Dictionary<int, double> eIdHashCodes2_ = new Dictionary<int, double>(EIdHashCodes2);

            //*
            foreach (var eIdHc1 in EIdHashCodes1) //reduce minor error, and accelerate
            {
                if (EIdHashCodes2.ContainsKey(eIdHc1.Key) && eIdHc1.Value == EIdHashCodes2[eIdHc1.Key])
                {
                    unchangedEIds.Add(eIdHc1.Key);
                    eIdHashCodes1_.Remove(eIdHc1.Key); eIdHashCodes2_.Remove(eIdHc1.Key);
                }
            } //*/

            Dictionary<int, double> _eIdHashCodes1_ = new Dictionary<int, double>(eIdHashCodes1_);
            foreach (var eIdHc1 in _eIdHashCodes1_)
            {
                foreach (var eIdHc2 in eIdHashCodes2_)
                {
                    if (eIdHc1.Value == eIdHc2.Value)
                    {
                        unchangedEIds.Add(eIdHc2.Key);
                        eIdHashCodes1_.Remove(eIdHc1.Key); eIdHashCodes2_.Remove(eIdHc2.Key);
                        if (eIdHc1.Key != eIdHc2.Key) EIdChangesFrom.Add(eIdHc2.Key, eIdHc1.Key);
                        break;
                    }
                }
            }

            foreach (var eIdHc1 in eIdHashCodes1_)
            {
                if (eIdHashCodes2_.ContainsKey(eIdHc1.Key))
                { modifyEIds.Add(eIdHc1.Key); eIdHashCodes2_.Remove(eIdHc1.Key); }
                else
                    delEIds.Add(eIdHc1.Key);
            }
            addEIds = eIdHashCodes2_.Select(idhc => idhc.Key).ToList();

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }

        #region Algorithm screenshots in paper
        void classifyChanges_C_paper(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();

            //use eIdEles_ to replace EIdEles in foreach, for Remove()
            var eIdEles1_ = new Dictionary<int, Element>(EIdEles1);
            var eIdEles2_ = new Dictionary<int, Element>(EIdEles2);

            foreach (var ee1 in EIdEles1)
                foreach (var ee2 in eIdEles2_)
                    if (eleEqual(ee1.Value, ee2.Value))
                    {
                        unchangedEIds.Add(ee2.Key);
                        eIdEles1_.Remove(ee1.Key); eIdEles2_.Remove(ee2.Key);
                        break;
                    }
            foreach (var ee1 in eIdEles1_)
            {
                if (eIdEles2_.ContainsKey(ee1.Key))
                { modifyEIds.Add(ee1.Key); eIdEles2_.Remove(ee1.Key); }
                else
                    delEIds.Add(ee1.Key);
            }
            foreach (var ee2 in eIdEles2_)
                addEIds.Add(ee2.Key);

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }
        void classifyChanges_CHc_paper(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();
            //EleHashCodes1/2 have been initialized
            var eIdHCs1_ = new Dictionary<int, double>(EIdHashCodes1);
            var eIdHCs2_ = new Dictionary<int, double>(EIdHashCodes2);

            foreach (var eIdHc1 in EIdHashCodes1)
                foreach (var eIdHc2 in eIdHCs2_)
                    if (eIdHc1.Value == eIdHc2.Value &&
                        eleEqual(EIdEles1[eIdHc1.Key], EIdEles2[eIdHc2.Key]))
                    {
                        unchangedEIds.Add(eIdHc2.Key);
                        eIdHCs1_.Remove(eIdHc1.Key); eIdHCs2_.Remove(eIdHc2.Key);
                        break;
                    }

            foreach (var eIdHc1 in eIdHCs1_)
            {
                if (eIdHCs2_.ContainsKey(eIdHc1.Key))
                { modifyEIds.Add(eIdHc1.Key); eIdHCs2_.Remove(eIdHc1.Key); }
                else
                    delEIds.Add(eIdHc1.Key);
            }
            addEIds = eIdHCs2_.Select(idhc => idhc.Key).ToList();

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }
        void classifyChanges_CHc_quick_paper(out List<int> unchangedEIds, out List<int> modifyEIds, out List<int> addEIds, out List<int> delEIds, out List<int> allEIds)
        {
            unchangedEIds = new List<int>(); modifyEIds = new List<int>(); addEIds = new List<int>(); delEIds = new List<int>(); allEIds = new List<int>();
            EIdChangesFrom = new Dictionary<int, int>();
            //EleHashCodes1/2 have been initialized
            //quick: assume no hash collision will happen
            var eIdHC1_ = new Dictionary<int, double>(EIdHashCodes1);
            var eIdHC2_ = new Dictionary<int, double>(EIdHashCodes2);

            foreach (var eIdHc1 in EIdHashCodes1)
                foreach (var eIdHc2 in eIdHC2_)
                    if (eIdHc1.Value == eIdHc2.Value)
                    {
                        unchangedEIds.Add(eIdHc2.Key);
                        eIdHC1_.Remove(eIdHc1.Key); eIdHC2_.Remove(eIdHc2.Key);
                        break;
                    }

            foreach (var eIdHc1 in eIdHC1_)
            {
                if (eIdHC2_.ContainsKey(eIdHc1.Key))
                { modifyEIds.Add(eIdHc1.Key); eIdHC2_.Remove(eIdHc1.Key); }
                else
                    delEIds.Add(eIdHc1.Key);
            }
            addEIds = eIdHC2_.Select(idhc => idhc.Key).ToList();

            unchangedEIds.Sort(); modifyEIds.Sort(); addEIds.Sort(); delEIds.Sort();
            allEIds = modifyEIds.Union(addEIds).Union(delEIds).Union(unchangedEIds).ToList();
        }
        #endregion

        void setLVAllByEIds(ref List<int> eIds)
        {
            MW.lVAll.Items.Clear();

            for (int i = 0; i < eIds.Count; i++)
            {
                int eId = eIds[i];
                string changeType = "=", name, category;

                if (DelEIds.Contains(eId))
                {
                    changeType = "-";
                    name = EIdEles1[eId].Name;
                    category = EIdEles1[eId]?.Category?.Name ?? "Null";
                }
                else
                {
                    if (ModifyEIds.Contains(eId))
                    { changeType = "*"; }
                    else if (AddEIds.Contains(eId))
                    { changeType = "+"; }
                    //if(UnchangeEIds.Contains(eId)) #assert
                    name = EIdEles2[eId].Name;
                    category = EIdEles2[eId]?.Category?.Name ?? "Null";
                }

                MW.lVAll.Items.Add(new LvElement { Number = 0, EId = eId, Name = name, ChangeType = changeType, Category = category, ChangeTypeNum = LvElement.getChangeTypeNum(changeType) });
            }

            MW.lVAll.Items.SortDescriptions.Add(new SortDescription("ChangeTypeNum", ListSortDirection.Ascending));
            MW.lVAll.Items.SortDescriptions.Add(new SortDescription("EId", ListSortDirection.Ascending));
            int n = 1;
            foreach (LvElement lvi in MW.lVAll.Items)
                lvi.Number = n++;

        }

        public void initTrVAll()
        {
            List<int> allCategoryIds = CategoryIds1.Intersect(CategoryIds2).ToList();

            List<TrVElement> trVEles = new List<TrVElement>();
            foreach (int cId in allCategoryIds)
            {
                List<int> eIds_c = getEIdsByCategory(cId);
                int allCount = eIds_c.Count;
                int modifyCount = eIds_c.Intersect(ModifyEIds).Count();
                int delCount = eIds_c.Intersect(DelEIds).Count();
                int addCount = eIds_c.Intersect(AddEIds).Count();

                string name = "Others";
                if (cId != CategoryIdNullHashCode)
                    name = ((BuiltInCategory)cId).ToString().Replace("OST_", "");

                TrVElement t = new TrVElement { CategoryId = cId, Header = name, AllCount = allCount, ModifyCount = modifyCount, AddCount = addCount, DelCount = delCount };
                trVEles.Add(t);
            }
            trVEles = trVEles.OrderByDescending(t => (t.ModifyCount + t.AddCount + t.DelCount)).ToList();

            TrVElement root = new TrVElement
            {
                CategoryId = 1,
                Header = "All",
                Children = trVEles,
                IsChecked = true,
                AllCount = AllEIds.Count,
                ModifyCount = ModifyEIds.Count,
                AddCount = AddEIds.Count,
                DelCount = DelEIds.Count
            };

            MW.trVAllVM.trVAllItems = new ObservableCollection<TrVElement> { root };
            MW.setTrVAllAbstract();
        }

        public void refreshLVByTrV()
        {
            TrVElement root = MW.trVAllVM.trVAllItems[0];
            if (root.IsChecked == true)
            {
                setLVAllByEIds(ref AllEIds);
            }
            else
            {
                List<int> xEIds = new List<int>();
                foreach (TrVElement t in root.Children)
                {
                    if (t.IsChecked == true)
                        xEIds = xEIds.Concat(getEIdsByCategory(t.CategoryId)).ToList();
                }
                xEIds = xEIds.Distinct().ToList();
                setLVAllByEIds(ref xEIds);
            }

        }

        List<int> getEIdsByCategory(int categoryId)
        {
            if (categoryId == 1)
                return AllEIds;

            List<int> eIds_c = new List<int>();
            for (int i = 0; i < CategoryIds2.Count; i++)
            {
                if (CategoryIds2[i] == categoryId)
                    eIds_c.Add(EIds2[i]);
            }
            for (int i = 0; i < CategoryIds1.Count; i++)
            {
                if (CategoryIds1[i] == categoryId && !EIdChangesFrom.ContainsValue(EIds1[i]))
                    eIds_c.Add(EIds1[i]);
            }

            return eIds_c.Distinct().ToList();
        }

        public StringBuilder getOneTypeElesInfo(List<int> xEIds, string changeType)
        {
            StringBuilder info = new StringBuilder(xEIds.Count * 200);
            string changeTypeStr = changeType == "*" ? "modified" : (changeType == "+" ? "added" : "deleted");
            info.Append(xEIds.Count + " elements are " + changeTypeStr + "\n");

            foreach (int eId in xEIds)
                info.Append("\n").Append(getEIdInfo(eId, changeType, false, " | "));

            return info;
        }

        public StringBuilder getEIdInfo(int eId, string changeType, bool isGetUnchangedInfo = true, string sep = "\n")
        {
            StringBuilder info = new StringBuilder(100);
            List<string> _infos = new List<string>(20);
            Element e0 = null, e2 = null;

            if (changeType == "*")
            {
                e0 = EIdEles1[eId]; e2 = EIdEles2[eId];
                //_infos.Add(getParamInfo(sep, "HashCode", EIdHashCodes1[eId].ToString("G16"), EIdHashCodes2[eId].ToString("G16")));
                _infos.Add(getParamInfo(sep, "Name", e0.Name, e2.Name));
                _infos.Add(getParamInfo(sep, "Location", getLocStr(e0), getLocStr(e2)));

                var params0 = getEleOrderedParams(e0); var params2 = getEleOrderedParams(e2);
                for (int i = 0; i < params0.Count(); i++)
                {
                    Parameter p0 = params0[i], p2 = params2[i];
                    _infos.Add(getParamInfo(sep, p0.Definition.Name, getParamStr(p0), getParamStr(p2)));
                }
            }
            else
            {
                double hc;
                if (changeType == "-")
                { e0 = EIdEles1[eId]; hc = EIdHashCodes1[eId]; }
                else//( "+" || "=" )
                { e0 = EIdEles2[eId]; hc = EIdHashCodes2[eId]; }

                //_infos.Add(getParamInfo(sep, "HashCode", hc.ToString("G16")));
                _infos.Add(getParamInfo(sep, "Name", e0.Name ?? ""));
                _infos.Add(getParamInfo(sep, "Location", getLocStr(e0)));

                var paras0 = getEleOrderedParams(e0);
                foreach (Parameter p0 in paras0)
                    _infos.Add(getParamInfo(sep, p0.Definition.Name, getParamStr(p0)));
            }

            int oldEId = (EIdChangesFrom != null && EIdChangesFrom.ContainsKey(eId)) ? EIdChangesFrom[eId] : eId;
            info.Append(getParamInfo(sep, "ID", oldEId.ToString(), eId.ToString()));

            string changedInfo = string.Join("", _infos.Where(s => s[0] != ' '));
            string unchangedInfo = string.Join("", _infos.Where(s => s[0] == ' '));
            if (changedInfo != "")
                info.Append("\n\n===(Changed properties)===").Append(changedInfo);
            if (isGetUnchangedInfo && unchangedInfo != "")
                info.Append("\n\n===(Unchanged properties)===").Append(unchangedInfo); //unchanged

            return info;
        }

        string getParamInfo(string sep, string name, string value1, string value2 = "")
        {
            if (value2 != "" && value2 != value1)
                return sep + name + ": " + value1 + " -> " + value2;
            else
                return " " + sep + name + ": " + value1;
        }

        public void highlightEle(List<int> eIds)
        {
            UIDocument uiDoc = UIApp.ActiveUIDocument;
            List<ElementId> elemIds = eIds.Select(eId => new ElementId(eId)).Where(elemId => uiDoc.Document.GetElement(elemId) != null).ToList();

            if (elemIds.Count == 0)
                return;

            try
            {
                UIApp.ActiveUIDocument.Selection.SetElementIds(elemIds);
                UIApp.ActiveUIDocument.ShowElements(elemIds);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            /*
            FilteredElementCollector fillPatternFilter = new FilteredElementCollector(Doc2);//过滤填充图案
            fillPatternFilter.OfClass(typeof(FillPatternElement));
            FillPatternElement fp = fillPatternFilter.First(m => (m as FillPatternElement).GetFillPattern().IsSolidFill) as FillPatternElement;//获取实体填充

            View v = Doc2.ActiveView; //Doc2必须为当前打开的文件
            Autodesk.Revit.DB.Color color = new Autodesk.Revit.DB.Color(255, 0, 0);
            
            foreach (int eId in eIds)
            {
                ElementId elemId = new ElementId(eId);
                OverrideGraphicSettings ogs = v.GetElementOverrides(elemId);
                ogs.SetProjectionFillPatternId(fp.Id); //设置 投影/表面 ->填充图案->填充图案            
                ogs.SetProjectionFillColor(color); //设置 投影/表面 ->填充图案->颜色
                ogs.SetProjectionFillPatternVisible(true);
                ogs.SetProjectionLineColor(color);
                v.SetElementOverrides(elemId, ogs); //应用到视图
            }*/

        }

        bool isMeaningfulParam(Parameter p1)
        {
            if (p1 == null || p1.Definition == null)// || !p1.HasValue
                return false;

            string defName = p1.Definition.Name;
            if (defName == "标记" || defName == "文件路径")//CHS
                return false;

            if (defName == "File Path")//EN
                return false;

            return true;
        }

        //BE CAREFUL that the method use by HashCode==Equal==getStr.
        #region HashCode
        double getEleHashCode(Element e1, ref Dictionary<int, double> eIdHashCodes)
        {
            int eId = e1.Id.IntegerValue;
            if (eIdHashCodes.ContainsKey(eId))
                return eIdHashCodes[eId];

            //contains: category, location (geometry), params, name
            double hashCode = 0.0;
            double[] hcs = new double[5];//for debug
            eIdHashCodes.Add(eId, hashCode); //避免出现循环调用，即Ele在求HashCode时，其某个Param的的Id为其自身
            hashCode += e1.Category?.Id.IntegerValue ?? CategoryHashCode; hcs[0] = hashCode;
            hashCode += getLocHashCode(e1.Location); hcs[1] = hashCode - hcs[0];
            hashCode += getParamsHashCode(getEleOrderedParams(e1)); hcs[2] = hashCode - hcs[1];
            hashCode += Math.Pow(e1.Name.GetHashCode(), 2) % 10000000000; hcs[3] = hashCode - hcs[2];
            eIdHashCodes[eId] = hashCode; hcs[4] = hashCode;

            //string _infos = string.Join("\t", hcs);//#
            //string paramsStr = getParamsStr(getEleOrderedParams(e1));//#

            return hashCode;
        }

        double getParamsHashCode(List<Parameter> paramL)
        {
            double hashCode = ParameterHashCode;
            List<double> hcs = new List<double>();//for debugging
            hcs.Add(hashCode);
            foreach (Parameter p1 in paramL)
            {
                if (isMeaningfulParam(p1))
                    hashCode += getParamHashCode(p1);
                else
                    hashCode += p1.Definition?.Name.GetHashCode() * 1e-6 ?? ParameterHashCode;

                hcs.Add(hashCode - hcs[hcs.Count - 1]);
            }

            //string _infos = string.Join("\t", hcs);//#
            return hashCode;
        }

        double getParamHashCode(Parameter p1)
        {
            double paramDefHc = p1.Definition.Name.GetHashCode() * 1e-6;

            switch (p1.StorageType)
            {
                case StorageType.Integer:
                    return paramDefHc * (p1.AsInteger() + 10);
                case StorageType.Double:
                    return paramDefHc * (Math.Round(p1.AsDouble(), 6) + 10);
                case StorageType.String:
                    string str = p1.AsString(); //p1.AsString()=="" is equal to null
                    return (str == null || str == "") ? paramDefHc : paramDefHc * str.GetHashCode();
                case StorageType.ElementId:
                    string str2 = p1.AsValueString(); //here, elementId doesn't refer to element
                    return (str2 == null || str2 == "") ? paramDefHc : paramDefHc * str2.GetHashCode();
                default:
                    return paramDefHc * ParameterHashCode; //for StorageType.None
            }
        }

        double getLocHashCode(Location loc1)
        {
            if (loc1 == null)
                return LocationHashCode;

            if (loc1.GetType() == typeof(LocationCurve))
            {
                Curve c1 = (loc1 as LocationCurve).Curve;
                try
                {
                    return 10 * getXYZHashCode(c1.GetEndPoint(0)) + getXYZHashCode(c1.GetEndPoint(1)) + 100 * Math.Round(c1.Length, 6);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) //The input curve is not bound.
                {
                    return 100 * Math.Round(c1.Length, 6) + 7;
                }
            }
            else if (loc1.GetType() == typeof(LocationPoint))
            {
                XYZ p1 = (loc1 as LocationPoint).Point;
                return getXYZHashCode(p1);
            }
            else
            {
                return LocationHashCode * 2;
            }

        }

        double getXYZHashCode(XYZ p1)
        {
            return 100 * Math.Round(p1.X, 6) + 10 * Math.Round(p1.Y, 6) + Math.Round(p1.Z, 6);
        }

        #endregion

        #region Equal
        bool eleEqual(Element e1, Element e2)
        {
            //if (!eIdEqual(e1.Id, e2.Id))
            //    return false;
            if (e1.Name != e2.Name)
                return false;

            if (!eIdEqual(e1.Category?.Id, e2.Category?.Id))
                return false;
            //if (!eIdEqual(e1?.LevelId, e2?.LevelId))
            //    return false;
            if (!locEqual(e1, e2))
                return false;

            List<Parameter> params1 = getEleOrderedParams(e1);
            List<Parameter> params2 = getEleOrderedParams(e2);
            if (params1.Count != params2.Count)
                return false;

            for (int i = 0; i < params1.Count; i++)
            {
                if (isMeaningfulParam(params1[i]) != isMeaningfulParam(params2[i]))
                    return false;
                if (isMeaningfulParam(params1[i]) && isMeaningfulParam(params2[i]) && !paramEqual(params1[i], params2[i]))
                    return false;
            }

            return true;
        }

        bool eleEqual_Full(Element e1, Element e2)
        {
            //if (!eIdEqual(e1.Id, e2.Id))
            //    return false;
            if (e1.Name != e2.Name)
                return false;

            if (!eIdEqual(e1.Category?.Id, e2.Category?.Id))
                return false;
            if (!locEqual(e1, e2))
                return false;


            //if (!eIdEqual(e1?.LevelId, e2?.LevelId))
            //    return false;
            //if (!eIdEqual(e1?.AssemblyInstanceId, e2?.AssemblyInstanceId))
            //    return false;
            //if (!eIdEqual(e1?.CreatedPhaseId, e2?.CreatedPhaseId))
            //    return false;
            //if (!eIdEqual(e1?.DemolishedPhaseId, e2?.DemolishedPhaseId))
            //    return false;
            //if (!eIdEqual(e1?.GroupId, e2?.GroupId))
            //    return false;
            //if (!eIdEqual(e1?.OwnerViewId, e2?.OwnerViewId))
            //    return false;
            //if (e1.WorksetId != e2.WorksetId)
            //    return false;

            List<Parameter> params1 = getEleOrderedParams(e1);
            List<Parameter> params2 = getEleOrderedParams(e2);
            if (params1.Count != params2.Count)
                return false;

            for (int i = 0; i < params1.Count; i++)
            {
                if (isMeaningfulParam(params1[i]) != isMeaningfulParam(params2[i]))
                    return false;
                if (!paramEqual_Full(params1[i], params2[i]))
                    return false;
            }

            return true;
        }

        bool paramEqual(Parameter p1, Parameter p2)
        {
            //null is regarded as equal to ""
            //if (p1 == null || p2 == null)
            //    return p1 == null && p2 == null;

            if (p1.StorageType != p2.StorageType)
                return false;

            switch (p1.StorageType)
            {
                case StorageType.None:
                    return p2.StorageType == StorageType.None;
                case StorageType.Integer:
                    return p1.AsInteger() == p2.AsInteger();
                case StorageType.Double:
                    return Math.Abs(p1.AsDouble() - p2.AsDouble()) < (1e-6);
                case StorageType.String:
                    return (p1.AsString() ?? "") == (p2.AsString() ?? ""); //for string, null is the same as ""
                case StorageType.ElementId:
                    return (p1.AsValueString() ?? "") == (p2.AsValueString() ?? "");
                    //return p1.AsElementId().IntegerValue == p2.AsElementId().IntegerValue;
            }
            return false;
        }

        bool paramEqual_Full(Parameter p1, Parameter p2)
        {
            if (p1 == null || p2 == null)
                return p1 == null && p2 == null;

            if (p1.Definition != p2.Definition)
                return false;

            if (p1.StorageType != p2.StorageType)
                return false;

            switch (p1.StorageType)
            {
                case StorageType.None:
                    return p2.StorageType == StorageType.None;
                case StorageType.Integer:
                    return p1.AsInteger() == p2.AsInteger();
                case StorageType.Double:
                    return Math.Abs(p1.AsDouble() - p2.AsDouble()) < (1e-6);
                case StorageType.String:
                    return (p1.AsString() ?? "") == (p2.AsString() ?? ""); //for string, null is the same as ""
                case StorageType.ElementId:
                    return (p1.AsValueString() ?? "") == (p2.AsValueString() ?? "");
                    //return p1.AsElementId().IntegerValue == p2.AsElementId().IntegerValue;
            }
            return false;
        }


        bool eIdEqual(ElementId eId1, ElementId eId2)
        {
            if (eId1 == null || eId2 == null)
                return (eId1 == null && eId2 == null);

            return eId1.IntegerValue == eId2.IntegerValue;
        }

        bool locEqual(Element e1, Element e2)
        {
            if (e1.Location == null || e2.Location == null)
                return (e1.Location == null && e2.Location == null);

            if (e1.Location.GetType() != e2.Location.GetType())
                return false;

            if (e1.Location.GetType() == typeof(LocationCurve))
            {
                Curve c1 = (e1.Location as LocationCurve).Curve;
                Curve c2 = (e2.Location as LocationCurve).Curve;
                try
                {
                    return xyzEqual(c1.GetEndPoint(0), c2.GetEndPoint(0)) && xyzEqual(c1.GetEndPoint(1), c2.GetEndPoint(1)) && Math.Round(c1.Length, 6) == Math.Round(c2.Length, 6);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) //The input curve is not bound.
                {
                    return Math.Round(c1.Length, 6) == Math.Round(c2.Length, 6);
                }
            }

            else if (e1.Location.GetType() == typeof(LocationPoint))
            {
                XYZ p1 = (e1.Location as LocationPoint).Point;
                XYZ p2 = (e2.Location as LocationPoint).Point;
                return xyzEqual(p1, p2);
            }

            return e1.Location.ToString() == e2.Location.ToString();
        }

        bool xyzEqual(XYZ p1, XYZ p2)
        {
            return (Math.Round(p1.X, 6) == Math.Round(p2.X, 6) && Math.Round(p1.Y, 6) == Math.Round(p2.Y, 6)
                && Math.Round(p1.Z, 6) == Math.Round(p2.Z, 6));
        }
        #endregion

        string getParamsStr(List<Parameter> paramL)
        {
            string s = "";
            foreach (var p1 in paramL)
                s += p1.Definition.Name + ": " + getParamStr(p1) + "\n";

            return s;
        }

        string getParamStr(Parameter p1)
        {
            switch (p1.StorageType)
            {
                case StorageType.Integer:
                    return p1.AsInteger().ToString();
                case StorageType.Double:
                    return p1.AsDouble().ToString("f6");
                case StorageType.String:
                    return p1.AsString();
                case StorageType.ElementId:
                    return p1.AsValueString() + " (ElementId=" + p1.AsElementId().IntegerValue + ") ";
                default:
                    return "None"; //for StorageType.None
            }
        }

        string getLocStr(Element e1)
        {
            if (e1.Location == null)
                return "";

            if (e1.Location.GetType() == typeof(LocationCurve))
            {
                Curve c1 = (e1.Location as LocationCurve).Curve;
                try
                {
                    return "{起点=" + c1.GetEndPoint(0).ToString() + "，终点=" + c1.GetEndPoint(1).ToString() + "，长度=" + c1.Length + "}";
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) //The input curve is not bound
                {
                    return "{Curve not bound，长度=" + c1.Length + "}";
                }
                //getXYZStr()
            }

            else if (e1.Location.GetType() == typeof(LocationPoint))
            {
                XYZ p1 = (e1.Location as LocationPoint).Point;
                return p1.ToString();
            }

            return "";
        }

        List<Parameter> getEleOrderedParams(Element elem)
        {
            List<Parameter> paramL = new List<Parameter>(10);
            ParameterSet ps = elem.Parameters;
            foreach (Parameter p in ps)
                paramL.Add(p);

            var lParam_Ordered = paramL.OrderBy(p => p.Id.IntegerValue);
            return lParam_Ordered.ToList();
        }

        void printStatus(string msg, bool isAdd = false)
        {
            string originTitle = "RevitDiff";
            if (!isAdd)
                MW.Title = originTitle + " [" + msg + "] ";
            else
                MW.Title += " | " + msg;
        }

        void _test1()
        {
            printStatus("Test");

            Stopwatch stopwatch = new Stopwatch();
            int N = (int)1e4;
            string msg = "";
            int j = 0;

            //stopwatch.Restart();
            //for (int i = 0; i < N / 10; i++)
            //{
            //    j = 0;
            //    foreach (Parameter p in Eles1[i].Parameters)
            //    {
            //        paramEqual(p, p);
            //        if (++j >= 10) break;
            //    }
            //}
            //stopwatch.Stop();
            //msg += "read Parameter *1e4" + "\t| " + stopwatch.Elapsed.TotalSeconds + "s\n";

            stopwatch.Restart();
            int n1 = 0;
            foreach (var d in EIdHashCodes1)
            {
                if (d.Key == d.Key) ;

                if (++n1 == N) break;
            }
            stopwatch.Stop();
            msg += "double Equal" + "\t| " + stopwatch.Elapsed.TotalSeconds + "s\n";

            stopwatch.Restart();
            for (int i = 0; i < n1; i++)
            {
                if (eleEqual(Eles1[i], Eles2[i])) ;
            }
            stopwatch.Stop();
            msg += "eleEqual" + "\t| " + stopwatch.Elapsed.TotalSeconds + "\n";

            /*
            read ID *1e6	    | 0.0093001s
            read Parameter *1e6	| 0.1075151s
            read Int *1e6	    | 5.99E-05s
            [Int:ID:P = 1:150:1600]

            double Equal	| 0.0001158
            ele Equal	    | 6.5662168
            [double:eleEqual = 56700]
             */
            Clipboard.SetText(msg);
            MessageBox.Show(msg);
        }

        void _testAllFiles(string path)
        {
            printStatus("Test");

            string dirPath = path.Substring(0, path.LastIndexOf('\\')) + '\\';
            string[] filePaths = Directory.GetFiles(dirPath, "*.rvt");

            List<string> msgs = new List<string>();
            msgs.Add(path.Substring(path.LastIndexOf('\\') + 1) + "\tM\tC\tHc\tQhc");

            foreach (string filePath in filePaths)
            {
                if (filePath == path)
                    continue;

                string msg1 = filePath.Substring(filePath.LastIndexOf('\\') + 1);
                int[] ii = { 0, 3 };
                foreach (int i in ii)
                {
                    MW.cbAlgorithm.SelectedIndex = i;
                    diffRvtDoc(path, filePath);
                    msg1 += "\t" + _getMeaningfulChangedEIdCount();
                }
                msgs.Add(msg1);
            }

            using (StreamWriter sw = new StreamWriter(path + ".txt"))
            {
                foreach (string msg1 in msgs)
                    sw.WriteLine(msg1);
            }

        }

        void _testAllDirFiles(string dirPath)
        {
            printStatus("Test");

            string[] dirPaths_ = Directory.GetDirectories(dirPath);
            List<string> msgs = new List<string>();
            msgs.Add("Test\tM\tQhc");

            foreach (string dirpath_ in dirPaths_)
            {
                string[] filePaths = Directory.GetFiles(dirpath_, "*.rvt");
                if (filePaths.Length != 2)
                    continue;

                string msg1 = dirpath_.Substring(dirpath_.LastIndexOf('\\') + 1);
                int[] ii = { 0, 3 };
                foreach (int i in ii)
                {
                    MW.cbAlgorithm.SelectedIndex = i;
                    diffRvtDoc(filePaths[0], filePaths[1]);
                    msg1 += "\t" + _getMeaningfulChangedEIdCount();
                }
                msgs.Add(msg1);
            }

            using (StreamWriter sw = new StreamWriter(dirPath + "\\result.txt"))
            {
                foreach (string msg1 in msgs)
                    sw.WriteLine(msg1);
            }

        }


        //不统计Meaningless Categories
        int _getMeaningfulChangedEIdCount()
        {
            TrVElement root = MW.trVAllVM.trVAllItems[0];
            int count = (ModifyEIds.Union(AddEIds).Union(DelEIds)).Count();

            foreach (TrVElement t in root.Children)
            {
                if (t.Header == "WeakDims" || t.Header == "SketchLines" || t.Header == "Views" || t.Header == "Cameras")
                    count -= (t.AddCount + t.DelCount + t.ModifyCount);
            }

            return count;
        }

    }

}

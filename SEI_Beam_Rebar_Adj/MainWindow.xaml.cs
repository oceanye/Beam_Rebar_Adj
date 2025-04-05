using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Geometry3d;
using System.Threading.Tasks;
using System.Windows.Threading;
using Vector = Tekla.Structures.Geometry3d.Vector;
using Point = Tekla.Structures.Geometry3d.Point;

namespace TeklaRebarAdjuster
{
    public partial class MainWindow : Window
    {
        // Tekla相关字段
        private Model _model;
        private Picker _picker;

        // Custom commands for keyboard shortcuts
        public static readonly RoutedCommand SelectPointCommand = new RoutedCommand("SelectPoint", typeof(MainWindow));
        public static readonly RoutedCommand AdjustEndpointsCommand = new RoutedCommand("AdjustEndpoints", typeof(MainWindow));

        // 当前选择的"钢筋"对象列表（可能是SingleRebar、RebarGroup等）
        private List<ModelObject> _selectedRebars = new List<ModelObject>();

        // 将“对象ID -> 对象端点列表”进行映射
        private Dictionary<int, List<Point>> _objectEndpoints = new Dictionary<int, List<Point>>();

        // 将“对象ID -> 方向向量(单位向量)”进行映射
        private Dictionary<int, Vector> _objectDirections = new Dictionary<int, Vector>();

        // 从界面输入的间隙值
        private double _gap = 0.0;

        // 从界面输入的左右容差值
        private double _offset = 50.0;

        // 最终用户在 Tekla 中点击选择的目标点
        private Point _selectedPoint = null;

        public MainWindow()
        {
            InitializeComponent();
            InitializeTeklaConnection();

            // Setup command bindings
            CommandBinding selectPointBinding = new CommandBinding(SelectPointCommand);
            selectPointBinding.Executed += BtnSelectPoint_Click;
            selectPointBinding.CanExecute += CanExecuteSelectPoint;
            this.CommandBindings.Add(selectPointBinding);

            CommandBinding adjustEndpointsBinding = new CommandBinding(AdjustEndpointsCommand);
            adjustEndpointsBinding.Executed += BtnAdjustEndpoints_Click;
            adjustEndpointsBinding.CanExecute += CanExecuteAdjustEndpoints;
            this.CommandBindings.Add(adjustEndpointsBinding);

            // Add keyboard event handler
            this.KeyDown += MainWindow_KeyDown;

            LogStatus("欢迎使用『Tekla钢筋端点（按ID区分）调整工具』。\n请先选择两个直线钢筋。");
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && btnSelectPoint.IsEnabled)
            {
                BtnSelectPoint_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Return && btnAdjustEndpoints.IsEnabled)
            {
                BtnAdjustEndpoints_Click(sender, null);
                e.Handled = true;
            }
        }

        // Tekla相关字段
        private void InitializeTeklaConnection()
        {
            try
            {
                _model = new Model();
                if (!_model.GetConnectionStatus())
                {
                    LogStatus("错误：无法连接到Tekla Structures。请确保Tekla正在运行。");
                    DisableAllButtons();
                    return;
                }

                _picker = new Picker();
                LogStatus("已成功连接到Tekla Structures。");
            }
            catch (Exception ex)
            {
                LogStatus($"初始化Tekla连接时出错：{ex.Message}");
                DisableAllButtons();
            }
        }

        /// <summary>
        /// 在日志中列出所有对象ID及其端点
        /// </summary>
        private void ShowAllEndpoints()
        {
            LogStatus("===== 当前提取到的对象及端点信息 =====");
            foreach (var kvp in _objectEndpoints)
            {
                int objId = kvp.Key;
                var endpoints = kvp.Value;
                LogStatus($"对象 {objId} 的端点:");

                int index = 0;
                foreach (var pt in endpoints)
                {
                    LogStatus($"  - 端点[{index}] = ({pt.X:F2}, {pt.Y:F2}, {pt.Z:F2})");
                    index++;
                }

                if (_objectDirections.ContainsKey(objId))
                {
                    var dir = _objectDirections[objId];
                    LogStatus($"  -> 方向向量 (单位向量): ({dir.X:F2}, {dir.Y:F2}, {dir.Z:F2})");
                }
            }
            LogStatus("====================================");
        }

        /// <summary>
        /// 点击按钮：选择钢筋
        /// </summary>
        private void BtnSelectRebars_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_picker == null || _model == null)
                {
                    LogStatus("系统未初始化，请确保已连接到Tekla。");
                    return;
                }

                btnSelectRebars.IsEnabled = false; // 防止重复点击

                LogStatus("请在Tekla模型中选择两个直线钢筋...");
                txtStatusBar.Text = "选择钢筋中...";

                // 清空之前的数据
                _selectedRebars.Clear();
                _objectEndpoints.Clear();
                _objectDirections.Clear();
                _selectedPoint = null;

                // 在新线程中执行选择（若您担心多线程与Tekla冲突，可改成在UI线程中执行）
                Thread t = new Thread(new ThreadStart(() =>
                {
                    try
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                LogStatus($"请选择第 {i + 1} 个钢筋...");
                            });

                            ModelObject selObj = null;
                            try
                            {
                                selObj = _picker.PickObject(Picker.PickObjectEnum.PICK_ONE_REINFORCEMENT);
                            }
                            catch
                            {
                                // 用户取消
                                Dispatcher.Invoke(() =>
                                {
                                    LogStatus("钢筋选择操作被取消。");
                                    btnSelectRebars.IsEnabled = true;
                                    CommandManager.InvalidateRequerySuggested();
                                });
                                return;
                            }

                            if (selObj != null)
                            {
                                _selectedRebars.Add(selObj);
                                Dispatcher.Invoke(() =>
                                {
                                    LogStatus($"已选择第 {i + 1} 个对象: ID={selObj.Identifier.ID}, Type={selObj.GetType().Name}");
                                    LogObjectDetails(selObj);
                                });
                            }
                        }

                        // 如果成功选了2个对象
                        if (_selectedRebars.Count == 2)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                ExtractEndpoints(_selectedRebars);
                                CalculateDirections();

                                LogStatus("已提取端点并计算方向。");
                                ShowAllEndpoints(); // 在日志输出当前的对象ID及对应端点

                                btnSelectPoint.IsEnabled = true;
                                btnMerge.IsEnabled = true;
                                CommandManager.InvalidateRequerySuggested();
                                txtStatusBar.Text = "选择钢筋完成";
                            });
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                LogStatus("未完成2个对象的选择。");
                                btnSelectRebars.IsEnabled = true;
                                CommandManager.InvalidateRequerySuggested();
                                txtStatusBar.Text = "对象选择未完成";
                            });
                        }
                    }
                    catch (Exception ex2)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LogStatus($"选择线程出错：{ex2.Message}");
                            btnSelectRebars.IsEnabled = true;
                            CommandManager.InvalidateRequerySuggested();
                        });
                    }
                }));
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                LogStatus($"选择时出错：{ex.Message}");
                btnSelectRebars.IsEnabled = true;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>
        /// 从选择的对象中提取端点到 _objectEndpoints
        /// 假设每个对象都为直线钢筋，只取起点与终点
        /// </summary>
        private void ExtractEndpoints(List<ModelObject> selectedObjects)
        {
            _objectEndpoints.Clear();

            foreach (var obj in selectedObjects)
            {
                int id = obj.Identifier.ID;
                if (!_objectEndpoints.ContainsKey(id))
                {
                    _objectEndpoints[id] = new List<Point>();
                }

                if (obj is SingleRebar singleRebar && singleRebar.Polygon != null)
                {
                    ArrayList pts = singleRebar.Polygon.Points;
                    if (pts.Count >= 2)
                    {
                        Point start = (Point)pts[0];
                        Point end = (Point)pts[pts.Count - 1];

                        // 复制一份，避免引用一致
                        _objectEndpoints[id].Add(new Point(start.X, start.Y, start.Z));
                        _objectEndpoints[id].Add(new Point(end.X, end.Y, end.Z));
                    }
                }
                else if (obj is RebarGroup rebarGroup && rebarGroup.Polygons != null)
                {
                    // 如果是 RebarGroup（且直线），理论上 polygons 里也有起点和终点，示例仅取第一个多边形的首尾
                    if (rebarGroup.Polygons.Count > 0)
                    {
                        var poly = rebarGroup.Polygons[0] as Polygon;
                        if (poly != null && poly.Points.Count >= 2)
                        {
                            Point start = (Point)poly.Points[0];
                            Point end = (Point)poly.Points[poly.Points.Count - 1];

                            _objectEndpoints[id].Add(new Point(start.X, start.Y, start.Z));
                            _objectEndpoints[id].Add(new Point(end.X, end.Y, end.Z));
                        }
                    }
                }
                else
                {
                    LogStatus($"对象ID={id} ({obj.GetType().Name}) 不符合直线钢筋的提取条件，可能无法提取端点。");
                }
            }
        }

        /// <summary>
        /// 以“对象ID”为单位，计算各对象的方向向量（单位向量）
        /// </summary>
        private void CalculateDirections()
        {
            _objectDirections.Clear();

            foreach (var kv in _objectEndpoints)
            {
                int objId = kv.Key;
                List<Point> endpoints = kv.Value;

                // 对于直线钢筋，应该只有2个端点
                if (endpoints.Count == 2)
                {
                    var p1 = endpoints[0];
                    var p2 = endpoints[1];

                    Vector dir = new Vector(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
                    double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
                    if (len > 1e-9)
                    {
                        dir.X /= len;
                        dir.Y /= len;
                        dir.Z /= len;
                    }
                    else
                    {
                        LogStatus($"警告：对象ID={objId}的长度过小，无法计算方向向量。");
                    }

                    _objectDirections[objId] = dir;
                }
                else
                {
                    LogStatus($"对象ID={objId}端点数={endpoints.Count}，无法计算方向向量（期望2个）。");
                }
            }
        }

        /// <summary>
        /// 在日志显示：每个对象到目标点最近的端点
        /// </summary>
        private void ShowClosestEndpoints(Point targetPoint)
        {
            var results = new List<(int ObjId, Point Endpoint, double Distance)>();

            foreach (var kvp in _objectEndpoints)
            {
                int objId = kvp.Key;
                var endpoints = kvp.Value;

                double minDist = double.MaxValue;
                Point closestPt = null;

                foreach (var ep in endpoints)
                {
                    double dist = Distance(ep, targetPoint);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        closestPt = ep;
                    }
                }

                if (closestPt != null)
                {
                    results.Add((objId, closestPt, minDist));
                }
            }

            results = results.OrderBy(r => r.Distance).ToList();

            LogStatus("======== 计算所有端点到目标点的距离 ========");
            foreach (var r in results)
            {
                LogStatus($"对象ID: {r.ObjId}, 最近端点=({r.Endpoint.X:F2}, {r.Endpoint.Y:F2}, {r.Endpoint.Z:F2}), 距离={r.Distance:F2}");
            }
            LogStatus("=============================================");
        }

        /// <summary>
        /// 点击按钮：选择目标点
        /// </summary>
        private void BtnSelectPoint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_picker == null || _model == null)
                {
                    LogStatus("系统未初始化，请确保已连接到Tekla。");
                    return;
                }

                btnSelectPoint.IsEnabled = false;

                LogStatus("请在Tekla模型中选择一个目标点...");
                txtStatusBar.Text = "选择目标点中...";

                Thread pickPointThread = new Thread(() =>
                {
                    try
                    {
                        Point chosenPoint = null;
                        try
                        {
                            chosenPoint = _picker.PickPoint("请选择要移动到的目标点");
                        }
                        catch
                        {
                            // 用户取消
                            Dispatcher.Invoke(() =>
                            {
                                LogStatus("目标点选择被取消。");
                                btnSelectPoint.IsEnabled = true;
                                CommandManager.InvalidateRequerySuggested();
                            });
                            return;
                        }

                        if (chosenPoint != null)
                        {
                            _selectedPoint = new Point(chosenPoint.X, chosenPoint.Y, chosenPoint.Z);

                            Dispatcher.Invoke(() =>
                            {
                                LogStatus($"已选择目标点：({_selectedPoint.X:F2}, {_selectedPoint.Y:F2}, {_selectedPoint.Z:F2})");
                                // 计算各对象的最近端点
                                ShowClosestEndpoints(_selectedPoint);

                                btnAdjustEndpoints.IsEnabled = true;
                                txtStatusBar.Text = "目标点选择完成";
                            });
                        }
                    }
                    catch (Exception ex2)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LogStatus($"目标点选择线程异常：{ex2.Message}");
                            btnSelectPoint.IsEnabled = true;
                            CommandManager.InvalidateRequerySuggested();
                        });
                    }
                });
                pickPointThread.IsBackground = true;
                pickPointThread.Start();
            }
            catch (Exception ex)
            {
                LogStatus($"选择目标点错误：{ex.Message}");
                btnSelectPoint.IsEnabled = true;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>
        /// 当用户点击"调整端点"按钮时，执行端点调整逻辑。
        /// 假设我们只想让「最近的两根钢筋」在目标点处形成Gap。
        /// </summary>
        private void BtnAdjustEndpoints_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 从界面读取Gap
                if (!double.TryParse(txtGap.Text, out _gap))
                {
                    _gap = 0.0;
                    LogStatus("输入的间隙值无效，已设为 0。");
                }

                if (_model == null || _selectedPoint == null)
                {
                    LogStatus("数据不完整，无法进行调整。请先选择对象和目标点。");
                    return;
                }

                LogStatus($"开始调整端点，Gap = {_gap:F2} mm");
                txtStatusBar.Text = "端点调整中...";

                // 1) 找到所有对象到目标点的“最近端点”
                var allClosest = new List<(int ObjId, Point ClosestEndpoint, double Distance)>();

                foreach (var kvp in _objectEndpoints)
                {
                    int objId = kvp.Key;
                    List<Point> endpoints = kvp.Value;

                    double minDist = double.MaxValue;
                    Point bestPt = null;

                    // 找到“最近端点”
                    foreach (var ep in endpoints)
                    {
                        double dist = Distance(ep, _selectedPoint);
                        if (dist < minDist)
                        {
                            minDist = dist;
                            bestPt = ep;
                        }
                    }

                    if (bestPt != null)
                        allClosest.Add((objId, bestPt, minDist));
                }

                // 2) 按距离排序
                allClosest = allClosest.OrderBy(x => x.Distance).ToList();

                // 3) 示例：只移动最近的两个对象，使之产生 gap
                if (allClosest.Count >= 2)
                {
                    var first = allClosest[0]; // 最近
                    var second = allClosest[1]; // 第二近

                    LogStatus($"最近的两个对象 ID: {first.ObjId}, {second.ObjId}");

                    // 钢筋A 反向移动 halfGap
                    // (例如 false表示负向)
                    AdjustObjectEndpoint(first.ObjId, first.ClosestEndpoint, isPositiveDirection: false);

                    // 钢筋B 正向移动 halfGap
                    AdjustObjectEndpoint(second.ObjId, second.ClosestEndpoint, isPositiveDirection: true);
                }
                else if (allClosest.Count == 1)
                {
                    // 只有一个对象，就直接移动过去(或只做负向移动)
                    var single = allClosest[0];
                    LogStatus($"仅有1个对象 ID={single.ObjId}, 无法形成Gap，只做单个移动。");
                    AdjustObjectEndpoint(single.ObjId, single.ClosestEndpoint, isPositiveDirection: false);
                }
                else
                {
                    LogStatus("找不到任何对象端点，无法执行移动。");
                }

                // 4) 提交
                _model.CommitChanges();
                LogStatus("端点修改已提交到Tekla模型。");

                // 复位按钮等UI
                txtStatusBar.Text = "操作完成";
                btnSelectRebars.IsEnabled = true;
                btnSelectPoint.IsEnabled = false;
                btnAdjustEndpoints.IsEnabled = false;
                btnMerge.IsEnabled = false;  
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                LogStatus($"调整端点出错: {ex.Message}\n{ex.StackTrace}");
                txtStatusBar.Text = "发生错误";
            }
        }

        /// <summary>
        /// 调整某对象的端点 oldEndpoint，使之沿自身方向向量±(gap/2)移动，
        // 并以所选目标点 _selectedPoint 进行投影.
        /// isPositiveDirection = true表示正向，false表示负向
        /// </summary>
        private void AdjustObjectEndpoint(int objId, Point oldEndpoint, bool isPositiveDirection)
        {
            // 找到对应对象
            var obj = _selectedRebars.FirstOrDefault(o => o.Identifier.ID == objId);
            if (obj == null)
            {
                LogStatus($"在已选对象中未找到ID={objId}，无法移动。");
                return;
            }

            // 找到方向向量(单位向量)
            if (!_objectDirections.ContainsKey(objId))
            {
                LogStatus($"对象ID={objId}无方向向量，将直接移动到目标点。");
                MoveEndpoint(obj, oldEndpoint, _selectedPoint);
                return;
            }

            Vector dir = _objectDirections[objId];

            // 计算: oldEndpoint -> _selectedPoint 的向量
            Vector toTarget = new Vector(
                _selectedPoint.X - oldEndpoint.X,
                _selectedPoint.Y - oldEndpoint.Y,
                _selectedPoint.Z - oldEndpoint.Z
            );

            // 点积(投影长度)
            double projectionLen = toTarget.X * dir.X + toTarget.Y * dir.Y + toTarget.Z * dir.Z;

            // 投影点：oldEndpoint + projectionLen * dir
            Point projected = new Point(
                oldEndpoint.X + projectionLen * dir.X,
                oldEndpoint.Y + projectionLen * dir.Y,
                oldEndpoint.Z + projectionLen * dir.Z
            );

            // 在投影点的基础上，前/后移 halfGap
            double halfGap = _gap / 2.0;
            double sign = isPositiveDirection ? 1.0 : -1.0;

            Point final = new Point(
                projected.X + sign * halfGap * dir.X,
                projected.Y + sign * halfGap * dir.Y,
                projected.Z + sign * halfGap * dir.Z
            );

            // 调用MoveEndpoint，替换多边形数据并Modify()
            MoveEndpoint(obj, oldEndpoint, final);
        }

        /// <summary>
        /// 将某个对象（SingleRebar、RebarGroup等）中的指定旧端点移动到新位置
        /// </summary>
        private void MoveEndpoint(ModelObject obj, Point oldPoint, Point newPoint)
        {
            if (obj is SingleRebar singleRebar)
            {
                if (singleRebar.Polygon != null && singleRebar.Polygon.Points != null)
                {
                    ArrayList newPts = new ArrayList();
                    bool changed = false;
                    for (int i = 0; i < singleRebar.Polygon.Points.Count; i++)
                    {
                        Point cur = (Point)singleRebar.Polygon.Points[i];
                        if (ArePointsEqual(cur, oldPoint))
                        {
                            newPts.Add(new Point(newPoint.X, newPoint.Y, newPoint.Z));
                            changed = true;
                        }
                        else
                        {
                            newPts.Add(new Point(cur.X, cur.Y, cur.Z));
                        }
                    }
                    if (changed)
                    {
                        Polygon newPoly = new Polygon();
                        newPoly.Points = newPts;
                        singleRebar.Polygon = newPoly;
                        singleRebar.Modify();
                        LogStatus($"SingleRebar (ID={obj.Identifier.ID}) 端点已调整。");
                    }
                }
            }
            else if (obj is RebarGroup rebarGroup)
            {
                // 类似逻辑，遍历 Polygons
                bool groupChanged = false;
                ArrayList newPolygons = new ArrayList();

                foreach (Polygon poly in rebarGroup.Polygons)
                {
                    ArrayList polyPts = new ArrayList();
                    bool polyChanged = false;

                    for (int i = 0; i < poly.Points.Count; i++)
                    {
                        Point cur = (Point)poly.Points[i];
                        if (ArePointsEqual(cur, oldPoint))
                        {
                            polyPts.Add(new Point(newPoint.X, newPoint.Y, newPoint.Z));
                            polyChanged = true;
                        }
                        else
                        {
                            polyPts.Add(new Point(cur.X, cur.Y, cur.Z));
                        }
                    }

                    Polygon newPoly = new Polygon();
                    newPoly.Points = polyPts;
                    newPolygons.Add(newPoly);

                    if (polyChanged) groupChanged = true;
                }

                if (groupChanged)
                {
                    rebarGroup.Polygons.Clear();
                    foreach (Polygon p in newPolygons) rebarGroup.Polygons.Add(p);

                    rebarGroup.Modify();
                    LogStatus($"RebarGroup (ID={obj.Identifier.ID}) 端点已调整。");
                }
            }
            else
            {
                LogStatus($"不支持的对象类型: {obj.GetType().Name}, 暂不移动端点。");
            }
        }

        /// <summary>
        /// 计算两点距离
        /// </summary>
        private double Distance(Point p1, Point p2)
        {
            if (p1 == null || p2 == null) return double.MaxValue;
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double dz = p2.Z - p1.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// 判断两点是否相等（带公差）
        /// </summary>
        private bool ArePointsEqual(Point p1, Point p2, double tol = 0.001)
        {
            if (p1 == null || p2 == null) return false;
            return (Math.Abs(p1.X - p2.X) < tol &&
                    Math.Abs(p1.Y - p2.Y) < tol &&
                    Math.Abs(p1.Z - p2.Z) < tol);
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _selectedRebars.Clear();
            _objectEndpoints.Clear();
            _objectDirections.Clear();
            _selectedPoint = null;

            btnSelectRebars.IsEnabled = true;
            btnSelectPoint.IsEnabled = false;
            btnAdjustEndpoints.IsEnabled = false;
            btnMerge.IsEnabled = false;  
            CommandManager.InvalidateRequerySuggested();
            LogStatus("已重置。请重新选择对象。");
            txtStatusBar.Text = "就绪";
        }

        private void DisableAllButtons()
        {
            btnSelectRebars.IsEnabled = false;
            btnSelectPoint.IsEnabled = false;
            btnAdjustEndpoints.IsEnabled = false;
            btnMerge.IsEnabled = false;  
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// 输出对象的一些信息，方便调试
        /// </summary>
        private void LogObjectDetails(ModelObject obj)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"===== 对象 {obj.Identifier.ID} 详细信息 =====");
            sb.AppendLine($"Type: {obj.GetType().Name}");

            if (obj is Reinforcement reinf)
            {
                string name = string.Empty;
                double diameter = 0.0;
                reinf.GetReportProperty("NAME", ref name);
                reinf.GetReportProperty("DIAMETER", ref diameter);

                sb.AppendLine($"名称: {name}, 直径: {diameter}");

                if (obj is SingleRebar sr && sr.Polygon != null)
                {
                    sb.AppendLine($"SingleRebar.Polygon 点数: {sr.Polygon.Points.Count}");
                }
                else if (obj is RebarGroup rg && rg.Polygons != null)
                {
                    sb.AppendLine($"RebarGroup.Polygons 数量: {rg.Polygons.Count}");
                }
            }
            else
            {
                sb.AppendLine("不是钢筋对象。");
            }
            sb.AppendLine("==========================================");

            LogStatus(sb.ToString());
        }

        private void LogStatus(string message)
        {
            // 假设 txtStatus 是界面上的 TextBox, scrollViewer 是其滚动容器
            txtStatus.AppendText(message + "\n");
            scrollViewer.ScrollToVerticalOffset(double.MaxValue);
        }

        /// <summary>
        /// 点击按钮：合并钢筋
        /// </summary>
        private void BtnMerge_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_model == null || _selectedRebars.Count != 2)
                {
                    LogStatus("请先选择两个钢筋后再执行合并操作。");
                    return;
                }

                LogStatus("开始执行钢筋合并操作...");
                txtStatusBar.Text = "合并钢筋中...";

                // 获取两个钢筋的ID
                int firstId = _selectedRebars[0].Identifier.ID;
                int secondId = _selectedRebars[1].Identifier.ID;

                if (!_objectEndpoints.ContainsKey(firstId) || !_objectEndpoints.ContainsKey(secondId))
                {
                    LogStatus("无法获取钢筋端点，合并失败。");
                    return;
                }

                // 找到两个钢筋中距离最远的两点
                var pointPair = FindFarthestPointPair(firstId, secondId);
                if (pointPair == null)
                {
                    LogStatus("寻找最远点对失败，无法完成合并。");
                    return;
                }

                Point startPoint = pointPair.Item1;
                Point endPoint = pointPair.Item2;

                LogStatus($"找到距离最远的两点：");
                LogStatus($"起点: ({startPoint.X:F2}, {startPoint.Y:F2}, {startPoint.Z:F2})");
                LogStatus($"终点: ({endPoint.X:F2}, {endPoint.Y:F2}, {endPoint.Z:F2})");

                // 获取第二个钢筋（将要保留的钢筋）
                var targetRebar = _selectedRebars[1];

                // 更新第二个钢筋的端点
                UpdateRebarEndpoints(targetRebar, startPoint, endPoint);

                // 删除第一个钢筋
                _selectedRebars[0].Delete();

                // 提交更改
                _model.CommitChanges();

                // 清理选择和数据
                _selectedRebars.Remove(_selectedRebars[0]);
                _objectEndpoints.Remove(firstId);
                _objectDirections.Remove(firstId);

                LogStatus("钢筋合并完成，已删除钢筋1，并更新钢筋2的端点。");
                txtStatusBar.Text = "钢筋合并完成";
                CommandManager.InvalidateRequerySuggested();

                // 重新计算方向向量（因为钢筋2的端点已变化）
                CalculateDirections();
            }
            catch (Exception ex)
            {
                LogStatus($"合并钢筋时出错: {ex.Message}\n{ex.StackTrace}");
                txtStatusBar.Text = "合并操作出错";
            }
        }

        /// <summary>
        /// 查找两个钢筋中距离最远的点对
        /// </summary>
        private Tuple<Point, Point> FindFarthestPointPair(int firstId, int secondId)
        {
            List<Point> pointsA = _objectEndpoints[firstId];
            List<Point> pointsB = _objectEndpoints[secondId];

            Point maxP1 = null;
            Point maxP2 = null;
            double maxDistance = 0;

            // 遍历所有点对，找到距离最远的两点
            foreach (var p1 in pointsA)
            {
                foreach (var p2 in pointsB)
                {
                    double dist = Distance(p1, p2);
                    if (dist > maxDistance)
                    {
                        maxDistance = dist;
                        maxP1 = p1;
                        maxP2 = p2;
                    }
                }
            }

            // 遍历第一个钢筋内部端点对
            for (int i = 0; i < pointsA.Count; i++)
            {
                for (int j = i + 1; j < pointsA.Count; j++)
                {
                    double dist = Distance(pointsA[i], pointsA[j]);
                    if (dist > maxDistance)
                    {
                        maxDistance = dist;
                        maxP1 = pointsA[i];
                        maxP2 = pointsA[j];
                    }
                }
            }

            // 遍历第二个钢筋内部端点对
            for (int i = 0; i < pointsB.Count; i++)
            {
                for (int j = i + 1; j < pointsB.Count; j++)
                {
                    double dist = Distance(pointsB[i], pointsB[j]);
                    if (dist > maxDistance)
                    {
                        maxDistance = dist;
                        maxP1 = pointsB[i];
                        maxP2 = pointsB[j];
                    }
                }
            }

            if (maxP1 == null || maxP2 == null)
                return null;

            return new Tuple<Point, Point>(
                new Point(maxP1.X, maxP1.Y, maxP1.Z),
                new Point(maxP2.X, maxP2.Y, maxP2.Z)
            );
        }

        /// <summary>
        /// 更新钢筋的起点和终点
        /// </summary>
        private void UpdateRebarEndpoints(ModelObject rebar, Point startPoint, Point endPoint)
        {
            if (rebar is SingleRebar singleRebar)
            {
                if (singleRebar.Polygon != null && singleRebar.Polygon.Points != null)
                {
                    ArrayList newPts = new ArrayList();
                    // 只保留起点和终点
                    newPts.Add(new Point(startPoint.X, startPoint.Y, startPoint.Z));
                    newPts.Add(new Point(endPoint.X, endPoint.Y, endPoint.Z));

                    Polygon newPoly = new Polygon();
                    newPoly.Points = newPts;
                    singleRebar.Polygon = newPoly;
                    singleRebar.Modify();
                    LogStatus($"SingleRebar (ID={rebar.Identifier.ID}) 端点已更新。");
                }
            }
            else if (rebar is RebarGroup rebarGroup)
            {
                // 对于钢筋组，更新所有多边形
                ArrayList newPolygons = new ArrayList();

                foreach (Polygon poly in rebarGroup.Polygons)
                {
                    Polygon newPoly = new Polygon();
                    ArrayList newPoints = new ArrayList();

                    // 只保留起点和终点
                    newPoints.Add(new Point(startPoint.X, startPoint.Y, startPoint.Z));
                    newPoints.Add(new Point(endPoint.X, endPoint.Y, endPoint.Z));

                    newPoly.Points = newPoints;
                    newPolygons.Add(newPoly);
                }

                rebarGroup.Polygons.Clear();
                foreach (Polygon p in newPolygons) rebarGroup.Polygons.Add(p);

                rebarGroup.Modify();
                LogStatus($"RebarGroup (ID={rebar.Identifier.ID}) 端点已更新。");
            }
            else
            {
                LogStatus($"不支持的对象类型: {rebar.GetType().Name}, 无法更新端点。");
            }
        }

        private void CanExecuteSelectPoint(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = btnSelectPoint.IsEnabled;
        }

        private void CanExecuteAdjustEndpoints(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = btnAdjustEndpoints.IsEnabled;
        }

        private void SelectPoint_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            BtnSelectPoint_Click(sender, null);
        }

        private void AdjustEndpoints_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            BtnAdjustEndpoints_Click(sender, null);
        }

        /// <summary>
        /// 点击按钮：选择共线钢筋
        /// </summary>
        private void BtnSelectColinear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_model == null)
                {
                    LogStatus("系统未初始化，请确保已连接到Tekla。");
                    return;
                }

                // 清空之前的数据
                _selectedRebars.Clear();
                _objectEndpoints.Clear();
                _objectDirections.Clear();

                // 获取左右容差值
                if (!double.TryParse(txtOffset.Text, out _offset))
                {
                    LogStatus("错误：左右容差值必须是有效的数字。");
                    return;
                }

                txtStatusBar.Text = "选择参考钢筋中...";

                // 选择参考钢筋
                ModelObject referenceRebar = _picker.PickObject(Picker.PickObjectEnum.PICK_ONE_REINFORCEMENT) as ModelObject;
                if (referenceRebar == null)
                {
                    LogStatus("未选择任何钢筋或选择的对象不是有效的钢筋。");
                    return;
                }

                // 获取参考钢筋的端点和方向
                List<Point> refEndpoints = GetRebarEndpoints(referenceRebar);
                if (refEndpoints == null || refEndpoints.Count < 2)
                {
                    LogStatus("无法获取参考钢筋的端点。");
                    return;
                }

                Vector refDirection = GetRebarDirection(refEndpoints[0], refEndpoints[1]);
                Point refMidPoint = new Point(
                    (refEndpoints[0].X + refEndpoints[1].X) / 2,
                    (refEndpoints[0].Y + refEndpoints[1].Y) / 2,
                    (refEndpoints[0].Z + refEndpoints[1].Z) / 2
                );

                // 选择主构件
                ModelObject mainObject = _picker.PickObject(Picker.PickObjectEnum.PICK_ONE_OBJECT);
                if (mainObject == null)
                {
                    LogStatus("错误：未选择有效的构件。");
                    return;
                }

                // 尝试获取构件组
                Assembly assembly = null;
                if (mainObject is Assembly)
                {
                    assembly = mainObject as Assembly;
                    LogStatus("已选择构件组。");
                }
                else if (mainObject is Part)
                {
                    assembly = new Assembly();
                    assembly.Add(mainObject as Part);
                    LogStatus("已选择单个构件并创建新构件组。");
                }
                else
                {
                    LogStatus($"错误：所选对象类型为 {mainObject.GetType().Name}，不是有效的构件或构件组。");
                    return;
                }

                if (assembly == null)
                {
                    LogStatus("错误：所选构件不属于任何构件组。");
                    return;
                }

                // 获取主构件
                Part mainPart = assembly.GetMainPart() as Part;
                if (mainPart == null)
                {
                    LogStatus($"错误：无法获取主构件。\n" +
                            $"构件组ID: {assembly.Identifier}\n" +
                            $"构件组类型: {assembly.GetType().Name}\n" +
                            $"GetMainPart()返回类型: {(assembly.GetMainPart()?.GetType().Name ?? "null")}");
                    return;
                }

                string mainPartProfile = "";
                mainPart.GetReportProperty("PROFILE", ref mainPartProfile);
                LogStatus($"已获取主构件:\n" +
                        $"ID: {mainPart.Identifier}\n" +
                        $"类型: {mainPart.GetType().Name}\n" +
                        $"Profile: {mainPartProfile}");

                // 查找所有钢筋
                ModelObjectEnumerator allRebars = _model.GetModelObjectSelector().GetAllObjects();

                int colinearCount = 0;
                int attachedCount = 0;
                int outsideBoundaryCount = 0;

                while (allRebars.MoveNext())
                {
                    ModelObject currentRebar = allRebars.Current as ModelObject;
                    if (currentRebar == null || currentRebar.Identifier.ID == referenceRebar.Identifier.ID)
                        continue;

                    if (currentRebar is Reinforcement)
                    {
                        List<Point> currentEndpoints = GetRebarEndpoints(currentRebar);
                        if (currentEndpoints == null || currentEndpoints.Count < 2)
                            continue;

                        Vector currentDirection = GetRebarDirection(currentEndpoints[0], currentEndpoints[1]);
                        Point currentMidPoint = new Point(
                            (currentEndpoints[0].X + currentEndpoints[1].X) / 2,
                            (currentEndpoints[0].Y + currentEndpoints[1].Y) / 2,
                            (currentEndpoints[0].Z + currentEndpoints[1].Z) / 2
                        );

                        // 检查方向是否平行（考虑正反方向）
                        double dotProduct = Math.Abs(refDirection.Dot(currentDirection));
                        if (Math.Abs(dotProduct - 1.0) < 0.001)
                        {
                            // 计算中点到参考线的垂直距离
                            double distance = GetPointToLineDistance(currentMidPoint, refEndpoints[0], refDirection);
                            
                            if (distance <= _offset)
                            {
                                PolyBeam polyBeam = currentRebar as PolyBeam;
                                if (polyBeam != null)
                                {
                                    // 获取多段钢梁的起点
                                    CoordinateSystem coordinates = polyBeam.GetCoordinateSystem();
                                    if (coordinates != null)
                                    {
                                        Point startPoint = coordinates.Origin;
                                        if (IsPointInBoundingBox(startPoint, mainPart))
                                        {
                                            assembly.Add(polyBeam);
                                            attachedCount++;
                                            LogStatus($"成功添加多段钢梁 ID:{polyBeam.Identifier}");
                                        }
                                        else
                                        {
                                            outsideBoundaryCount++;
                                            LogStatus($"多段钢梁 ID:{polyBeam.Identifier} 的起点在边界框外");
                                        }
                                    }
                                    else
                                    {
                                        LogStatus($"警告：无法获取多段钢梁 ID:{polyBeam.Identifier} 的坐标系统");
                                    }
                                }
                                colinearCount++;
                            }
                        }
                    }
                }

                if (attachedCount > 0)
                {
                    assembly.Modify();
                    _model.CommitChanges();
                }

                LogStatus($"共找到 {colinearCount} 个共线钢筋\n" +
                         $"成功附加 {attachedCount} 个多段钢梁\n" +
                         $"超出边界 {outsideBoundaryCount} 个多段钢梁");
            }
            catch (Exception ex)
            {
                LogStatus($"错误：{ex.Message}\n堆栈跟踪：{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 计算点到直线的垂直距离
        /// </summary>
        private double GetPointToLineDistance(Point point, Point linePoint, Vector lineDirection)
        {
            Vector pointVector = new Vector(
                point.X - linePoint.X,
                point.Y - linePoint.Y,
                point.Z - linePoint.Z
            );

            Vector crossProduct = lineDirection.Cross(pointVector);
            return crossProduct.GetLength();
        }

        /// <summary>
        /// 获取钢筋的方向向量（单位向量）
        /// </summary>
        private Vector GetRebarDirection(Point start, Point end)
        {
            Vector direction = new Vector(
                end.X - start.X,
                end.Y - start.Y,
                end.Z - start.Z
            );
            direction.Normalize();
            return direction;
        }

        private List<Point> GetRebarEndpoints(ModelObject rebar)
        {
            if (rebar is SingleRebar singleRebar && singleRebar.Polygon != null)
            {
                ArrayList pts = singleRebar.Polygon.Points;
                if (pts.Count >= 2)
                {
                    Point start = (Point)pts[0];
                    Point end = (Point)pts[pts.Count - 1];

                    return new List<Point> { new Point(start.X, start.Y, start.Z), new Point(end.X, end.Y, end.Z) };
                }
            }
            else if (rebar is RebarGroup rebarGroup && rebarGroup.Polygons != null)
            {
                if (rebarGroup.Polygons.Count > 0)
                {
                    var poly = rebarGroup.Polygons[0] as Polygon;
                    if (poly != null && poly.Points.Count >= 2)
                    {
                        Point start = (Point)poly.Points[0];
                        Point end = (Point)poly.Points[poly.Points.Count - 1];

                        return new List<Point> { new Point(start.X, start.Y, start.Z), new Point(end.X, end.Y, end.Z) };
                    }
                }
            }

            return null;
        }

        private void BtnAttachToAssembly_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 选择主构件
                ModelObject mainObject = _picker.PickObject(Picker.PickObjectEnum.PICK_ONE_OBJECT);
                if (mainObject == null)
                {
                    LogStatus("错误：未选择有效的构件。");
                    return;
                }

                // 尝试获取构件组
                Assembly assembly = null;
                if (mainObject is Assembly)
                {
                    assembly = mainObject as Assembly;
                    LogStatus("已选择构件组。");
                }
                else if (mainObject is Part)
                {
                    assembly = new Assembly();
                    assembly.Add(mainObject as Part);
                    LogStatus("已选择单个构件并创建新构件组。");
                }
                else
                {
                    LogStatus($"错误：所选对象类型为 {mainObject.GetType().Name}，不是有效的构件或构件组。");
                    return;
                }

                if (assembly == null)
                {
                    LogStatus("错误：所选构件不属于任何构件组。");
                    return;
                }

                // 获取主构件
                Part mainPart = assembly.GetMainPart() as Part;
                if (mainPart == null)
                {
                    LogStatus($"错误：无法获取主构件。\n" +
                            $"构件组ID: {assembly.Identifier}\n" +
                            $"构件组类型: {assembly.GetType().Name}\n" +
                            $"GetMainPart()返回类型: {(assembly.GetMainPart()?.GetType().Name ?? "null")}");
                    return;
                }

                string mainPartProfile = "";
                mainPart.GetReportProperty("PROFILE", ref mainPartProfile);
                LogStatus($"已获取主构件:\n" +
                        $"ID: {mainPart.Identifier}\n" +
                        $"类型: {mainPart.GetType().Name}\n" +
                        $"Profile: {mainPartProfile}");

                // 使用框选择过滤的对象
                string filterProfileString = txtFilter.Text.Trim();
                ModelObjectEnumerator selectedObjects = _picker.PickObjects(Picker.PickObjectsEnum.PICK_N_OBJECTS, "请框选要附加的多段钢梁构件");

                if (selectedObjects == null || !selectedObjects.MoveNext())
                {
                    LogStatus("错误：未选择任何构件。");
                    return;
                }

                int totalSelected = 0;
                int attachedCount = 0;
                int outsideBoundaryCount = 0;
                int wrongProfileCount = 0;
                int notPolyBeamCount = 0;
                Dictionary<string, int> profileCounts = new Dictionary<string, int>();

                do
                {
                    totalSelected++;
                    ModelObject obj = selectedObjects.Current;
                    PolyBeam polyBeam = obj as PolyBeam;
                    if (polyBeam != null)
                    {
                        string profileString = "";
                        polyBeam.GetReportProperty("PROFILE", ref profileString);
                        profileString = profileString.Trim();
                        
                        LogStatus($"\n检查多段钢梁 ID:{polyBeam.Identifier}");
                        LogStatus($"多段钢梁Profile: {profileString}");
                        
                        // 统计每种profile的数量
                        if (!profileCounts.ContainsKey(profileString))
                        {
                            profileCounts[profileString] = 0;
                        }
                        profileCounts[profileString]++;
                        
                        if (string.Equals(profileString, filterProfileString, StringComparison.OrdinalIgnoreCase))
                        {
                            // 获取多段钢梁的起点
                            CoordinateSystem coordinates = polyBeam.GetCoordinateSystem();
                            if (coordinates != null)
                            {
                                Point startPoint = coordinates.Origin;
                                if (IsPointInBoundingBox(startPoint, mainPart))
                                {
                                    assembly.Add(polyBeam);
                                    attachedCount++;
                                    LogStatus($"成功添加多段钢梁 ID:{polyBeam.Identifier}");
                                }
                                else
                                {
                                    outsideBoundaryCount++;
                                    LogStatus($"多段钢梁 ID:{polyBeam.Identifier} 的起点在边界框外");
                                }
                            }
                            else
                            {
                                LogStatus($"警告：无法获取多段钢梁 ID:{polyBeam.Identifier} 的坐标系统");
                            }
                        }
                        else
                        {
                            wrongProfileCount++;
                            LogStatus($"多段钢梁 ID:{polyBeam.Identifier} 的Profile不匹配");
                        }
                    }
                    else
                    {
                        notPolyBeamCount++;
                        LogStatus($"选择的对象不是多段钢梁: {obj.GetType().Name}");
                    }
                } while (selectedObjects.MoveNext());

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"总选择对象数: {totalSelected}");
                sb.AppendLine($"其中多段钢梁数量: {totalSelected - notPolyBeamCount}");
                sb.AppendLine($"非多段钢梁数量: {notPolyBeamCount}");
                sb.AppendLine($"成功附加数量: {attachedCount}");
                sb.AppendLine($"超出边界数量: {outsideBoundaryCount}");
                sb.AppendLine($"规格不匹配数量: {wrongProfileCount}");
                if (profileCounts.Any())
                {
                    sb.AppendLine("\n选择多段钢梁的Profile String统计:");
                    foreach (var kvp in profileCounts.OrderByDescending(x => x.Value))
                    {
                        sb.AppendLine($"- {kvp.Key}: {kvp.Value}个");
                    }
                }
                sb.AppendLine($"\n目标Profile String: {filterProfileString}");

                if (attachedCount > 0)
                {
                    assembly.Modify();
                    _model.CommitChanges();
                }

                LogStatus(sb.ToString());
            }
            catch (Exception ex)
            {
                LogStatus($"错误：{ex.Message}\n堆栈跟踪：{ex.StackTrace}");
            }
        }

        private bool IsPointInBoundingBox(Point point, Part mainPart)
        {
            try
            {
                double tolerance = Convert.ToDouble(txtOffset.Text);
                
                // 获取主构件的实体模型
                Solid mainSolid = mainPart.GetSolid();
                if (mainSolid == null)
                {
                    LogStatus($"警告：无法获取主构件(ID:{mainPart.Identifier})的实体模型");
                    return false;
                }

                Point mainMinPoint = mainSolid.MinimumPoint;
                Point mainMaxPoint = mainSolid.MaximumPoint;

                if (mainMinPoint == null || mainMaxPoint == null)
                {
                    LogStatus($"警告：无法获取主构件(ID:{mainPart.Identifier})的边界点");
                    return false;
                }

                // 添加容差到边界框
                Point boundingMinPoint = new Point(
                    mainMinPoint.X - tolerance,
                    mainMinPoint.Y - tolerance,
                    mainMinPoint.Z - tolerance
                );
                Point boundingMaxPoint = new Point(
                    mainMaxPoint.X + tolerance,
                    mainMaxPoint.Y + tolerance,
                    mainMaxPoint.Z + tolerance
                );

                // 检查点是否在扩展的边界框内
                bool isInside = point.X >= boundingMinPoint.X && point.X <= boundingMaxPoint.X &&
                              point.Y >= boundingMinPoint.Y && point.Y <= boundingMaxPoint.Y &&
                              point.Z >= boundingMinPoint.Z && point.Z <= boundingMaxPoint.Z;

                LogStatus($"检查点 ({point.X:F2}, {point.Y:F2}, {point.Z:F2}):\n" +
                         $"主构件边界框:\n" +
                         $"原始边界: X[{mainMinPoint.X:F2}, {mainMaxPoint.X:F2}] " +
                         $"Y[{mainMinPoint.Y:F2}, {mainMaxPoint.Y:F2}] " +
                         $"Z[{mainMinPoint.Z:F2}, {mainMaxPoint.Z:F2}]\n" +
                         $"扩展边界(含容差{tolerance}mm): X[{boundingMinPoint.X:F2}, {boundingMaxPoint.X:F2}] " +
                         $"Y[{boundingMinPoint.Y:F2}, {boundingMaxPoint.Y:F2}] " +
                         $"Z[{boundingMinPoint.Z:F2}, {boundingMaxPoint.Z:F2}]\n" +
                         $"结果: {(isInside ? "在内" : "在外")}");

                return isInside;
            }
            catch (Exception ex)
            {
                LogStatus($"检查点是否在边界框内时出错: {ex.Message}");
                return false;
            }
        }

        private Point GetPartCenterPoint(Part part)
        {
            try
            {
                // 获取构件的实体模型
                Solid solid = part.GetSolid();
                if (solid == null)
                {
                    LogStatus($"警告：无法获取构件(ID:{part.Identifier})的实体模型");
                    return null;
                }

                // 获取构件的边界框
                Point minPoint = solid.MinimumPoint;
                Point maxPoint = solid.MaximumPoint;

                if (minPoint == null || maxPoint == null)
                {
                    LogStatus($"警告：无法获取构件(ID:{part.Identifier})的边界点");
                    return null;
                }

                // 计算中心点
                Point center = new Point(
                    (minPoint.X + maxPoint.X) / 2,
                    (minPoint.Y + maxPoint.Y) / 2,
                    (minPoint.Z + maxPoint.Z) / 2
                );

                LogStatus($"构件(ID:{part.Identifier}):\n" +
                         $"边界框: X[{minPoint.X:F2}, {maxPoint.X:F2}] " +
                         $"Y[{minPoint.Y:F2}, {maxPoint.Y:F2}] " +
                         $"Z[{minPoint.Z:F2}, {maxPoint.Z:F2}]\n" +
                         $"中心点: ({center.X:F2}, {center.Y:F2}, {center.Z:F2})");
                return center;
            }
            catch (Exception ex)
            {
                LogStatus($"获取构件(ID:{part.Identifier})中心点时出错: {ex.Message}");
                return null;
            }
        }
    }
}
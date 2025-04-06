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

        // 水平焊接钢筋间距
        private const double WELDING_REBAR_SPACING = 200.0;

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

            chkAddWeldingRebar.Checked += chkAddWeldingRebar_Checked;

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
                Point bestPt = null;

                // 找到“最近端点”
                foreach (var ep in endpoints)
                {
                    double dist = Distance(ep, targetPoint);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        bestPt = ep;
                    }
                }

                if (bestPt != null)
                    results.Add((objId, bestPt, minDist));
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
                LogStatus("=== 开始调整钢筋 ===");
                if (!ValidateInputs())
                    return;

                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane());

                // 调整钢筋端点
                AdjustRebarEndpoints();

                // 检查焊接钢筋创建条件
                LogStatus("\n=== 检查焊接钢筋创建条件 ===");
                LogStatus($"1. 焊接钢筋复选框状态: {(chkAddWeldingRebar.IsChecked == true ? "已选中" : "未选中")}");
                LogStatus($"2. 间距值: {_gap}mm");

                if (chkAddWeldingRebar.IsChecked == true)
                {
                    if (_gap > 0)
                    {
                        LogStatus("✓ 条件满足，开始创建焊接钢筋...");
                        CreateWeldingRebars();
                    }
                    else
                    {
                        LogStatus("✗ 间距必须大于0，无法创建焊接钢筋");
                    }
                }
                else
                {
                    LogStatus("✗ 焊接钢筋复选框未选中，跳过创建");
                }

                LogStatus("\n=== 操作完成 ===");

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
                LogStatus($"错误：{ex.Message}\n{ex.StackTrace}");
            }
        }

        private bool ValidateInputs()
        {
            LogStatus("开始验证输入...");
            if (_selectedRebars.Count == 0)
            {
                LogStatus("错误：未选择钢筋");
                MessageBox.Show("请先选择钢筋！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (_selectedPoint == null)
            {
                LogStatus("错误：未选择点");
                MessageBox.Show("请先选择点！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!double.TryParse(txtGap.Text, out _gap))
            {
                LogStatus("错误：间距输入无效");
                MessageBox.Show("请输入有效的间距值！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (_gap < 0)
            {
                LogStatus("错误：间距不能为负");
                MessageBox.Show("间距不能为负值！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (chkAddWeldingRebar.IsChecked == true)
            {
                double weldingLength;
                if (!double.TryParse(txtWeldingLength.Text, out weldingLength))
                {
                    LogStatus("错误：焊接钢筋长度输入无效");
                    MessageBox.Show("请输入有效的焊接钢筋长度！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                if (weldingLength <= 0)
                {
                    LogStatus("错误：焊接钢筋长度必须大于0");
                    MessageBox.Show("焊接钢筋长度必须大于0！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }

            LogStatus($"输入验证通过。间距值: {_gap}");
            return true;
        }

        private void AdjustRebarEndpoints()
        {
            LogStatus($"开始调整端点，Gap = {_gap:F2} mm");
            txtStatusBar.Text = "端点调整中...";

            // 1) 找到所有对象到目标点的"最近端点"
            var allClosest = new List<(int ObjId, Point ClosestEndpoint, double Distance)>();

            foreach (var kvp in _objectEndpoints)
            {
                int objId = kvp.Key;
                var endpoints = kvp.Value;

                double minDist = double.MaxValue;
                Point bestPt = null;

                // 找到"最近端点"
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

                // 获取两个钢筋的端点和方向向量
                var firstEndpoints = _objectEndpoints[first.ObjId];
                var secondEndpoints = _objectEndpoints[second.ObjId];
                Vector firstDir = _objectDirections[first.ObjId];
                Vector secondDir = _objectDirections[second.ObjId];

                LogStatus("=== 移动前端点位置 ===");
                LogStatus($"钢筋 {first.ObjId} 端点:");
                LogStatus($"  - 端点[0] = ({firstEndpoints[0].X:F2}, {firstEndpoints[0].Y:F2}, {firstEndpoints[0].Z:F2})");
                LogStatus($"  - 端点[1] = ({firstEndpoints[1].X:F2}, {firstEndpoints[1].Y:F2}, {firstEndpoints[1].Z:F2})");
                LogStatus($"钢筋 {second.ObjId} 端点:");
                LogStatus($"  - 端点[0] = ({secondEndpoints[0].X:F2}, {secondEndpoints[0].Y:F2}, {secondEndpoints[0].Z:F2})");
                LogStatus($"  - 端点[1] = ({secondEndpoints[1].X:F2}, {secondEndpoints[1].Y:F2}, {secondEndpoints[1].Z:F2})");

                // 计算两根钢筋的中心点
                Point firstCenter = new Point(
                    (firstEndpoints[0].X + firstEndpoints[1].X) / 2,
                    (firstEndpoints[0].Y + firstEndpoints[1].Y) / 2,
                    (firstEndpoints[0].Z + firstEndpoints[1].Z) / 2
                );
                Point secondCenter = new Point(
                    (secondEndpoints[0].X + secondEndpoints[1].X) / 2,
                    (secondEndpoints[0].Y + secondEndpoints[1].Y) / 2,
                    (secondEndpoints[0].Z + secondEndpoints[1].Z) / 2
                );

                // 计算从选择点到中心点的向量
                Vector firstToCenter = new Vector(
                    firstCenter.X - _selectedPoint.X,
                    firstCenter.Y - _selectedPoint.Y,
                    firstCenter.Z - _selectedPoint.Z
                );
                Vector secondToCenter = new Vector(
                    secondCenter.X - _selectedPoint.X,
                    secondCenter.Y - _selectedPoint.Y,
                    secondCenter.Z - _selectedPoint.Z
                );

                // 标准化向量
                double firstLen = Math.Sqrt(firstToCenter.X * firstToCenter.X + firstToCenter.Y * firstToCenter.Y + firstToCenter.Z * firstToCenter.Z);
                double secondLen = Math.Sqrt(secondToCenter.X * secondToCenter.X + secondToCenter.Y * secondToCenter.Y + secondToCenter.Z * secondToCenter.Z);

                if (firstLen > 0)
                {
                    firstToCenter.X /= firstLen;
                    firstToCenter.Y /= firstLen;
                    firstToCenter.Z /= firstLen;
                }
                if (secondLen > 0)
                {
                    secondToCenter.X /= secondLen;
                    secondToCenter.Y /= secondLen;
                    secondToCenter.Z /= secondLen;
                }

                // 1. 先将两个端点都移动到选择点
                LogStatus("第一步：移动两个端点到选择点");
                MoveEndpoint(_selectedRebars.First(r => r.Identifier.ID == first.ObjId), first.ClosestEndpoint, _selectedPoint);
                MoveEndpoint(_selectedRebars.First(r => r.Identifier.ID == second.ObjId), second.ClosestEndpoint, _selectedPoint);
                UpdateEndpointCache(first.ObjId, first.ClosestEndpoint, _selectedPoint);
                UpdateEndpointCache(second.ObjId, second.ClosestEndpoint, _selectedPoint);

                // 2. 然后根据钢筋方向，将端点向外移动 gap/2
                LogStatus("第二步：将端点沿钢筋方向向外移动 gap/2");
                double halfGap = _gap / 2.0;

                // 确保方向向量指向远离选择点的方向
                double firstDot = firstDir.X * firstToCenter.X + firstDir.Y * firstToCenter.Y + firstDir.Z * firstToCenter.Z;
                double secondDot = secondDir.X * secondToCenter.X + secondDir.Y * secondToCenter.Y + secondDir.Z * secondToCenter.Z;

                if (firstDot < 0)
                    firstDir = new Vector(-firstDir.X, -firstDir.Y, -firstDir.Z);
                if (secondDot < 0)
                    secondDir = new Vector(-secondDir.X, -secondDir.Y, -secondDir.Z);

                Point firstTarget = new Point(
                    _selectedPoint.X + halfGap * firstDir.X,
                    _selectedPoint.Y + halfGap * firstDir.Y,
                    _selectedPoint.Z + halfGap * firstDir.Z
                );

                Point secondTarget = new Point(
                    _selectedPoint.X + halfGap * secondDir.X,
                    _selectedPoint.Y + halfGap * secondDir.Y,
                    _selectedPoint.Z + halfGap * secondDir.Z
                );

                MoveEndpoint(_selectedRebars.First(r => r.Identifier.ID == first.ObjId), _selectedPoint, firstTarget);
                MoveEndpoint(_selectedRebars.First(r => r.Identifier.ID == second.ObjId), _selectedPoint, secondTarget);
                UpdateEndpointCache(first.ObjId, _selectedPoint, firstTarget);
                UpdateEndpointCache(second.ObjId, _selectedPoint, secondTarget);

                LogStatus("=== 移动后端点位置 ===");
                firstEndpoints = _objectEndpoints[first.ObjId];  // 重新获取更新后的端点
                secondEndpoints = _objectEndpoints[second.ObjId];
                LogStatus($"钢筋 {first.ObjId} 端点:");
                LogStatus($"  - 端点[0] = ({firstEndpoints[0].X:F2}, {firstEndpoints[0].Y:F2}, {firstEndpoints[0].Z:F2})");
                LogStatus($"  - 端点[1] = ({firstEndpoints[1].X:F2}, {firstEndpoints[1].Y:F2}, {firstEndpoints[1].Z:F2})");
                LogStatus($"钢筋 {second.ObjId} 端点:");
                LogStatus($"  - 端点[0] = ({secondEndpoints[0].X:F2}, {secondEndpoints[0].Y:F2}, {secondEndpoints[0].Z:F2})");
                LogStatus($"  - 端点[1] = ({secondEndpoints[1].X:F2}, {secondEndpoints[1].Y:F2}, {secondEndpoints[1].Z:F2})");
            }
            else if (allClosest.Count == 1)
            {
                // 只有一个对象，就移动 gap/2 的距离
                var single = allClosest[0];
                LogStatus($"仅有1个对象 ID={single.ObjId}, 移动 gap/2 距离。");
                
                var endpoints = _objectEndpoints[single.ObjId];
                LogStatus("=== 移动前端点位置 ===");
                LogStatus($"钢筋 {single.ObjId} 端点:");
                LogStatus($"  - 端点[0] = ({endpoints[0].X:F2}, {endpoints[0].Y:F2}, {endpoints[0].Z:F2})");
                LogStatus($"  - 端点[1] = ({endpoints[1].X:F2}, {endpoints[1].Y:F2}, {endpoints[1].Z:F2})");
                
                Vector dir = _objectDirections[single.ObjId];
                double halfGap = _gap / 2.0;
                Point target = new Point(
                    _selectedPoint.X - halfGap * dir.X,
                    _selectedPoint.Y - halfGap * dir.Y,
                    _selectedPoint.Z - halfGap * dir.Z
                );
                
                MoveEndpoint(_selectedRebars.First(r => r.Identifier.ID == single.ObjId), single.ClosestEndpoint, target);
                UpdateEndpointCache(single.ObjId, single.ClosestEndpoint, target);
                
                endpoints = _objectEndpoints[single.ObjId];  // 重新获取更新后的端点
                LogStatus("=== 移动后端点位置 ===");
                LogStatus($"钢筋 {single.ObjId} 端点:");
                LogStatus($"  - 端点[0] = ({endpoints[0].X:F2}, {endpoints[0].Y:F2}, {endpoints[0].Z:F2})");
                LogStatus($"  - 端点[1] = ({endpoints[1].X:F2}, {endpoints[1].Y:F2}, {endpoints[1].Z:F2})");
            }
        }

        private void UpdateEndpointCache(int objId, Point oldEndpoint, Point newEndpoint)
        {
            if (_objectEndpoints.ContainsKey(objId))
            {
                var endpoints = _objectEndpoints[objId];
                for (int i = 0; i < endpoints.Count; i++)
                {
                    if (ArePointsEqual(endpoints[i], oldEndpoint))
                    {
                        endpoints[i] = newEndpoint;
                        break;
                    }
                }
            }
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
            try
            {
                if (obj is Reinforcement rebar)
                {
                    string size = "";
                    rebar.GetReportProperty("SIZE", ref size);
                    var match = System.Text.RegularExpressions.Regex.Match(size, @"\d+");
                    if (match.Success)
                    {
                        double diameter = double.Parse(match.Value);
                        // 更新间距为钢筋直径的1倍
                        txtGap.Text = diameter.ToString();

                        // 如果勾选了焊接钢筋，更新焊接钢筋长度为钢筋直径的10倍
                        if (chkAddWeldingRebar.IsChecked == true)
                        {
                            txtWeldingLength.Text = (diameter * 10).ToString();
                        }
                    }
                    else
                    {
                        LogStatus($"警告：无法从尺寸 '{size}' 解析出直径");
                    }

                    string phase = "";
                    rebar.GetReportProperty("PHASE", ref phase);
                    LogStatus($"钢筋信息：\n" +
                            $"ID: {rebar.Identifier.ID}\n" +
                            $"类型: {rebar.GetType().Name}\n" +
                            $"尺寸: {size}\n" +
                            $"阶段: {phase}");
                }
                else
                {
                    LogStatus($"选择的对象不是钢筋。类型: {obj.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                LogStatus($"获取对象信息时出错: {ex.Message}");
            }
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
                        double dotProduct = Math.Abs(refDirection.X * currentDirection.X + refDirection.Y * currentDirection.Y + refDirection.Z * currentDirection.Z);
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

        private void CreateWeldingRebars()
        {
            try
            {
                double weldingLength = Convert.ToDouble(txtWeldingLength.Text);
                LogStatus($"焊接钢筋长度: {weldingLength}mm");

                // 获取已选择的钢筋的属性作为模板
                if (_selectedRebars.Count == 0 || !(_selectedRebars[0] is Reinforcement templateRebar))
                {
                    LogStatus("错误：无法获取模板钢筋");
                    return;
                }

                // 获取钢筋直径
                string size = "";
                templateRebar.GetReportProperty("SIZE", ref size);
                var match = System.Text.RegularExpressions.Regex.Match(size, @"\d+");
                if (!match.Success)
                {
                    LogStatus($"错误：无法从尺寸 '{size}' 解析出直径");
                    return;
                }
                double diameter = double.Parse(match.Value);

                // 获取钢筋方向
                Vector direction = _objectDirections[_selectedRebars[0].Identifier.ID];
                
                // 计算垂直向量（用于偏移焊接钢筋）
                Vector perpendicular;
                if (chkVerticalWeldingRebar.IsChecked == true)
                {
                    // 如果选择垂直布置，则始终使用竖直向上的向量
                    perpendicular = new Vector(0, 0, 1);
                }
                else
                {
                    // 默认水平布置
                    perpendicular = new Vector(0, 1, 0); // 假设Y轴垂直于钢筋
                    if (Math.Abs(direction.Y) > Math.Abs(direction.X)) // 如果钢筋主要沿Y方向，则垂直向量为X轴
                    {
                        perpendicular = new Vector(1, 0, 0);
                    }
                }

                // 确保垂直向量真正垂直于钢筋方向
                double dotWithDirection = perpendicular.X * direction.X + perpendicular.Y * direction.Y + perpendicular.Z * direction.Z;
                perpendicular = new Vector(
                    perpendicular.X - dotWithDirection * direction.X,
                    perpendicular.Y - dotWithDirection * direction.Y,
                    perpendicular.Z - dotWithDirection * direction.Z
                );

                // 标准化垂直向量
                double perpLen = Math.Sqrt(perpendicular.X * perpendicular.X + perpendicular.Y * perpendicular.Y + perpendicular.Z * perpendicular.Z);
                if (perpLen > 0)
                {
                    perpendicular = new Vector(
                        perpendicular.X / perpLen,
                        perpendicular.Y / perpLen,
                        perpendicular.Z / perpLen
                    );
                }

                // 创建两根焊接钢筋
                for (int i = 0; i < 2; i++)
                {
                    SingleRebar weldingRebar = new SingleRebar();
                    weldingRebar.Father = templateRebar.Father; // 使用相同的父构件
                    weldingRebar.Class = 3; // Use class 3 for welding rebars
                    weldingRebar.Size = size;
                    string grade = "";
                    templateRebar.GetReportProperty("GRADE", ref grade);
                    weldingRebar.Grade = grade;
                    weldingRebar.Name = "WELDING_REBAR";
                    weldingRebar.NumberingSeries.StartNumber = i + 1;
                    weldingRebar.NumberingSeries.Prefix = "W";

                    // 计算焊接钢筋的位置
                    double offset = diameter * (i == 0 ? -1 : 1); // 第一根向左偏移，第二根向右偏移
                    Point center = new Point(
                        _selectedPoint.X + perpendicular.X * offset,
                        _selectedPoint.Y + perpendicular.Y * offset,
                        _selectedPoint.Z + perpendicular.Z * offset
                    );

                    // 创建焊接钢筋的多边形
                    Polygon polygon = new Polygon();
                    ArrayList points = new ArrayList();

                    // 焊接钢筋的起点和终点
                    Point startPoint = new Point(
                        center.X - direction.X * weldingLength / 2,
                        center.Y - direction.Y * weldingLength / 2,
                        center.Z - direction.Z * weldingLength / 2
                    );
                    Point endPoint = new Point(
                        center.X + direction.X * weldingLength / 2,
                        center.Y + direction.Y * weldingLength / 2,
                        center.Z + direction.Z * weldingLength / 2
                    );

                    points.Add(startPoint);
                    points.Add(endPoint);
                    polygon.Points = points;

                    weldingRebar.Polygon = polygon;
                    if (weldingRebar.Insert())
                    {
                        LogStatus($"成功创建焊接钢筋 {i + 1}，ID: {weldingRebar.Identifier.ID}");
                    }
                    else
                    {
                        LogStatus($"创建焊接钢筋 {i + 1} 失败");
                    }
                }

                _model.CommitChanges();
            }
            catch (Exception ex)
            {
                LogStatus($"创建焊接钢筋时出错: {ex.Message}");
            }
        }

        private void chkAddWeldingRebar_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedRebars.Count > 0 && _selectedRebars[0] is Reinforcement rebar)
                {
                    string size = "";
                    rebar.GetReportProperty("SIZE", ref size);
                    var match = System.Text.RegularExpressions.Regex.Match(size, @"\d+");
                    if (match.Success)
                    {
                        double diameter = double.Parse(match.Value);
                        txtWeldingLength.Text = (diameter * 8).ToString();
                        txtGap.Text = diameter.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"更新焊接钢筋参数时出错: {ex.Message}");
            }
        }
    }
}
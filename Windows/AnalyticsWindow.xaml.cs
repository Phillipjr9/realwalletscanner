using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public partial class AnalyticsWindow : Window
    {
        private DispatcherTimer _updateTimer;
        private List<double> _speedHistory = new List<double>();
        private DateTime _sessionStart;
        private WalletDatabase _database;

        public RecoveryStatistics Statistics { get; set; }
        public SessionManager.RecoverySession CurrentSession { get; set; }

        public AnalyticsWindow()
        {
            InitializeComponent();
            _sessionStart = DateTime.Now;
            _database = new WalletDatabase();
            
            // Start update timer
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            if (Statistics == null) return;

            try
            {
                // Update speed
                SpeedTextBlock.Text = $"{Statistics.WalletsPerSecond:N0} wallets/sec";
                
                // Track speed history for chart
                _speedHistory.Add(Statistics.WalletsPerSecond);
                if (_speedHistory.Count > 60) // Keep last 60 seconds
                    _speedHistory.RemoveAt(0);

                // Update total processed
                TotalProcessedTextBlock.Text = $"{Statistics.TotalChecked:N0}";
                TotalSubTextBlock.Text = $"Wallets checked since start";

                // Update success rate
                double successRate = Statistics.TotalChecked > 0 
                    ? (Statistics.SuccessRate * 100) 
                    : 0;
                SuccessRateTextBlock.Text = $"{successRate:F4}%";
                FoundCountTextBlock.Text = $"{Statistics.WalletsFound} wallets found with balance";

                // Update running time
                var elapsed = DateTime.Now - _sessionStart;
                RunningTimeTextBlock.Text = $"{elapsed:hh\\:mm\\:ss}";

                // Update ETA
                if (Statistics.EstimatedTimeRemaining.HasValue && Statistics.EstimatedTimeRemaining.Value < TimeSpan.MaxValue)
                {
                    var eta = Statistics.EstimatedTimeRemaining.Value;
                    if (eta.TotalDays > 1)
                        ETATextBlock.Text = $"ETA: {eta.TotalDays:F1} days remaining";
                    else if (eta.TotalHours > 1)
                        ETATextBlock.Text = $"ETA: {eta.TotalHours:F1} hours remaining";
                    else if (eta.TotalMinutes > 1)
                        ETATextBlock.Text = $"ETA: {eta.TotalMinutes:F0} minutes remaining";
                    else
                        ETATextBlock.Text = $"ETA: {eta.TotalSeconds:F0} seconds remaining";
                }
                else
                {
                    ETATextBlock.Text = "ETA: Calculating...";
                }

                // Update session info
                if (CurrentSession != null)
                {
                    SessionIdTextBlock.Text = CurrentSession.SessionId?.Length > 8 
                        ? CurrentSession.SessionId.Substring(0, 8) + "..." 
                        : CurrentSession.SessionId ?? "N/A";
                    StartTimeTextBlock.Text = CurrentSession.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
                    RecoveryTypeTextBlock.Text = CurrentSession.RecoveryType.ToString();
                }

                // Update thread count from settings
                var settings = AppSettings.Load();
                ThreadCountTextBlock.Text = $"{settings.ThreadCount} threads";

                // Update memory usage
                var process = Process.GetCurrentProcess();
                var memoryMB = process.WorkingSet64 / 1024 / 1024;
                MemoryUsageTextBlock.Text = $"{memoryMB:N0} MB";

                // Draw performance chart
                DrawPerformanceChart();
                
                // Draw USD history chart
                DrawUsdHistoryChart();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error updating analytics: {ex.Message}");
            }
        }

        private void DrawPerformanceChart()
        {
            PerformanceChart.Children.Clear();

            if (_speedHistory.Count < 2)
                return;

            double width = PerformanceChart.ActualWidth;
            double height = PerformanceChart.ActualHeight;

            if (width <= 0 || height <= 0)
                return;

            // Find max value for scaling
            double maxSpeed = _speedHistory.Max();
            if (maxSpeed == 0)
                maxSpeed = 1;

            // Draw grid lines
            for (int i = 0; i <= 4; i++)
            {
                double y = (height / 4) * i;
                var gridLine = new Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = Brushes.LightGray,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 5, 5 }
                };
                PerformanceChart.Children.Add(gridLine);

                // Add value labels
                double value = maxSpeed * (1 - (i / 4.0));
                var label = new System.Windows.Controls.TextBlock
                {
                    Text = $"{value:F0}",
                    FontSize = 10,
                    Foreground = Brushes.Gray
                };
                System.Windows.Controls.Canvas.SetLeft(label, 5);
                System.Windows.Controls.Canvas.SetTop(label, y - 10);
                PerformanceChart.Children.Add(label);
            }

            // Draw line chart
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                StrokeThickness = 2,
                Points = new PointCollection()
            };

            double xStep = width / Math.Max(_speedHistory.Count - 1, 1);

            for (int i = 0; i < _speedHistory.Count; i++)
            {
                double x = i * xStep;
                double y = height - ((_speedHistory[i] / maxSpeed) * height);
                polyline.Points.Add(new Point(x, y));
            }

            PerformanceChart.Children.Add(polyline);

            // Draw area under curve
            var polygon = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(30, 76, 175, 80)),
                Points = new PointCollection()
            };

            polygon.Points.Add(new Point(0, height));
            foreach (var point in polyline.Points)
            {
                polygon.Points.Add(point);
            }
            polygon.Points.Add(new Point(width, height));

            PerformanceChart.Children.Insert(0, polygon);
        }

        private void DrawUsdHistoryChart()
        {
            try
            {
                UsdHistoryChart.Children.Clear();

                var history = _database?.GetUsdHistory(30) ?? new List<WalletDatabase.UsdHistoryEntry>();
                
                // Update total USD text
                decimal totalUsd = history.Sum(h => h.UsdValue);
                TotalUsdTextBlock.Text = totalUsd > 0 ? $"Total: ${totalUsd:N2}" : "No wallets found yet";

                if (history.Count < 1)
                {
                    // Show "No data" message
                    var noDataLabel = new System.Windows.Controls.TextBlock
                    {
                        Text = "No USD history data available yet",
                        FontSize = 14,
                        Foreground = Brushes.Gray,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    System.Windows.Controls.Canvas.SetLeft(noDataLabel, 100);
                    System.Windows.Controls.Canvas.SetTop(noDataLabel, 80);
                    UsdHistoryChart.Children.Add(noDataLabel);
                    return;
                }

                double width = UsdHistoryChart.ActualWidth;
                double height = UsdHistoryChart.ActualHeight;

                if (width <= 0 || height <= 0)
                    return;

                // Group by date and sum USD values
                var dailyTotals = history
                    .GroupBy(h => h.Timestamp.Date)
                    .Select(g => new { Date = g.Key, TotalUsd = g.Sum(x => x.UsdValue) })
                    .OrderBy(x => x.Date)
                    .ToList();

                if (dailyTotals.Count == 0)
                    return;

                decimal maxUsd = dailyTotals.Max(d => d.TotalUsd);
                if (maxUsd == 0)
                    maxUsd = 1;

                // Draw grid lines
                for (int i = 0; i <= 4; i++)
                {
                    double y = (height / 4) * i;
                    var gridLine = new Line
                    {
                        X1 = 0,
                        Y1 = y,
                        X2 = width,
                        Y2 = y,
                        Stroke = Brushes.LightGray,
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 5, 5 }
                    };
                    UsdHistoryChart.Children.Add(gridLine);

                    // Add value labels
                    decimal value = maxUsd * (decimal)(1 - (i / 4.0));
                    var label = new System.Windows.Controls.TextBlock
                    {
                        Text = $"${value:N0}",
                        FontSize = 10,
                        Foreground = Brushes.Gray
                    };
                    System.Windows.Controls.Canvas.SetLeft(label, 5);
                    System.Windows.Controls.Canvas.SetTop(label, y - 10);
                    UsdHistoryChart.Children.Add(label);
                }

                // Draw bar chart for each day
                double barWidth = Math.Max(10, (width - 60) / dailyTotals.Count - 5);
                double xOffset = 50;

                for (int i = 0; i < dailyTotals.Count; i++)
                {
                    var day = dailyTotals[i];
                    double barHeight = (double)(day.TotalUsd / maxUsd) * (height - 30);
                    double x = xOffset + i * (barWidth + 5);
                    double y = height - barHeight - 20;

                    // Draw bar
                    var bar = new System.Windows.Shapes.Rectangle
                    {
                        Width = barWidth,
                        Height = Math.Max(2, barHeight),
                        Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                        RadiusX = 2,
                        RadiusY = 2
                    };
                    System.Windows.Controls.Canvas.SetLeft(bar, x);
                    System.Windows.Controls.Canvas.SetTop(bar, y);
                    UsdHistoryChart.Children.Add(bar);

                    // Add date label (show every few days if many entries)
                    if (dailyTotals.Count <= 10 || i % (dailyTotals.Count / 5 + 1) == 0)
                    {
                        var dateLabel = new System.Windows.Controls.TextBlock
                        {
                            Text = day.Date.ToString("MM/dd"),
                            FontSize = 9,
                            Foreground = Brushes.Gray
                        };
                        System.Windows.Controls.Canvas.SetLeft(dateLabel, x);
                        System.Windows.Controls.Canvas.SetTop(dateLabel, height - 18);
                        UsdHistoryChart.Children.Add(dateLabel);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error drawing USD history chart: {ex.Message}");
            }
        }

        private void RefreshData_Click(object sender, RoutedEventArgs e)
        {
            UpdateDisplay();
        }

        private void ExportReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var filename = $"Analytics_Report_{timestamp}.txt";
                
                var report = "=================================\n";
                report += "WALLET RECOVERY ANALYTICS REPORT\n";
                report += "=================================\n\n";
                report += $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n";
                report += "PERFORMANCE METRICS\n";
                report += "-------------------\n";
                report += $"Processing Speed: {Statistics?.WalletsPerSecond:N0} wallets/sec\n";
                report += $"Total Processed: {Statistics?.TotalChecked:N0}\n";
                report += $"Success Rate: {(Statistics?.SuccessRate ?? 0) * 100:F4}%\n";
                report += $"Wallets Found: {Statistics?.WalletsFound}\n";
                report += $"Running Time: {DateTime.Now - _sessionStart:hh\\:mm\\:ss}\n\n";
                
                if (CurrentSession != null)
                {
                    report += "SESSION INFORMATION\n";
                    report += "-------------------\n";
                    report += $"Session ID: {CurrentSession.SessionId}\n";
                    report += $"Start Time: {CurrentSession.StartTime:yyyy-MM-dd HH:mm:ss}\n";
                    report += $"Recovery Type: {CurrentSession.RecoveryType}\n";
                    report += $"Processed: {CurrentSession.ProcessedCount:N0} / {CurrentSession.TotalAttempts:N0}\n";
                }

                File.WriteAllText(filename, report);
                MessageBox.Show($"Report exported to:\n{filename}", "Export Successful", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting report: {ex.Message}", "Export Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _updateTimer?.Stop();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _updateTimer?.Stop();
            base.OnClosed(e);
        }
    }
}

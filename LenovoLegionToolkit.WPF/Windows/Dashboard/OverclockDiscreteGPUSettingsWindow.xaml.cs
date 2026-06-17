using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.WPF.Resources;
using Wpf.Ui.Controls;

namespace LenovoLegionToolkit.WPF.Windows.Dashboard;

public partial class OverclockDiscreteGPUSettingsWindow
{

    private readonly GPUOverclockController _gpuOverclockController = IoCContainer.Resolve<GPUOverclockController>();
    private bool _isUpdatingControls;

    public OverclockDiscreteGPUSettingsWindow()
    {
        InitializeComponent();

        var (enabled, info) = _gpuOverclockController.GetState();

        _applyCloseGrid.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _saveGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        _coreSlider.Maximum = GPUOverclockController.GetMaxCoreDeltaMhz();
        _memorySlider.Maximum = GPUOverclockController.GetMaxMemoryDeltaMhz();
        _coreNumberBox.Maximum = _coreSlider.Maximum;
        _memoryNumberBox.Maximum = _memorySlider.Maximum;
        SetCoreValue(info.CoreDeltaMhz);
        SetMemoryValue(info.MemoryDeltaMhz);

        Loaded += OverclockDiscreteGPUSettingsWindow_Loaded;
    }

    private async void OverclockDiscreteGPUSettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var maxDeltaMhz = await GPUOverclockController.GetMaxDeltaMhzAsync();

        _coreSlider.Maximum = maxDeltaMhz.CoreDeltaMhz;
        _coreNumberBox.Maximum = maxDeltaMhz.CoreDeltaMhz;
        if (_coreSlider.Value > _coreSlider.Maximum)
            SetCoreValue((int)_coreSlider.Maximum);

        _memorySlider.Maximum = maxDeltaMhz.MemoryDeltaMhz;
        _memoryNumberBox.Maximum = maxDeltaMhz.MemoryDeltaMhz;
        if (_memorySlider.Value > _memorySlider.Maximum)
            SetMemoryValue((int)_memorySlider.Maximum);

        var defaultDeltaMhz = await GPUOverclockController.GetDefaultDeltaMhzAsync();
        if (defaultDeltaMhz != GPUOverclockInfo.Zero && (int)_coreSlider.Value == 0 && (int)_memorySlider.Value == 0)
        {
            SetCoreValue(defaultDeltaMhz.CoreDeltaMhz);
            SetMemoryValue(defaultDeltaMhz.MemoryDeltaMhz);
        }
    }

    private void CoreSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingControls)
            return;

        SetCoreValue((int)e.NewValue);
    }

    private void MemorySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingControls)
            return;

        SetMemoryValue((int)e.NewValue);
    }

    private void CoreNumberBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingControls)
            return;

        UpdateSliderFromNumberBox(_coreNumberBox, _coreSlider);
    }

    private void MemoryNumberBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingControls)
            return;

        UpdateSliderFromNumberBox(_memoryNumberBox, _memorySlider);
    }

    private void CoreNumberBox_OnLostFocus(object sender, RoutedEventArgs e) => SetCoreValue((int)_coreSlider.Value);

    private void MemoryNumberBox_OnLostFocus(object sender, RoutedEventArgs e) => SetMemoryValue((int)_memorySlider.Value);

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        Save();
        await ApplyAsync();
    }

    private async void ApplyAndCloseButton_Click(object sender, RoutedEventArgs e)
    {
        Save();
        await ApplyAsync();
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Save();
        Close();
    }

    private void Save()
    {
        SetCoreValue((int)_coreSlider.Value);
        SetMemoryValue((int)_memorySlider.Value);

        var (enabled, _) = _gpuOverclockController.GetState();
        var info = new GPUOverclockInfo((int)_coreSlider.Value, (int)_memorySlider.Value);

        _gpuOverclockController.SaveState(enabled, info);
    }

    private async Task ApplyAsync() => await _gpuOverclockController.ApplyStateAsync();

    private void SetCoreValue(int value) => SetValue(_coreSlider, _coreNumberBox, value);

    private void SetMemoryValue(int value) => SetValue(_memorySlider, _memoryNumberBox, value);

    private void UpdateSliderFromNumberBox(NumberBox numberBox, Slider slider)
    {
        if (!int.TryParse(numberBox.Text, out var value))
            return;

        SetValue(slider, numberBox, value);
    }

    private void SetValue(Slider slider, NumberBox numberBox, int value)
    {
        _isUpdatingControls = true;

        try
        {
            var clamped = Math.Clamp(value, (int)slider.Minimum, (int)slider.Maximum);
            slider.Value = clamped;
            numberBox.Text = clamped.ToString();
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }
}

#if DEBUG
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Linux.Views.Settings;

internal sealed partial class ApplicationThemeEditorPage
{
    /// <summary>The <c>debug-theme-editor</c> operations that act on this page, through the widgets where one exists.</summary>
    internal void DebugRun(string operation, string[] arguments)
    {
        var vm = ViewModel;
        string Arg(int index) => arguments.Length > index ? arguments[index] : throw new ArgumentException($"{operation} needs argument {index}.");
        switch (operation)
        {
            case "name":
                vm.ThemeName = Arg(0);
                break;
            case "mode":
                _debugPaletteModes?.SetActive(string.Equals(Arg(0), "dark", StringComparison.OrdinalIgnoreCase) ? 1u : 0u);
                break;
            case "fit":
                _debugFit?.SetActive(string.Equals(Arg(0), "fit", StringComparison.OrdinalIgnoreCase) ? 1u : 0u);
                break;
            case "focal":
                var alignment = Enum.Parse<ThemeWallpaperAlignment>(Arg(0), true);
                _focalButtons[Array.IndexOf(FocalOrder, alignment)].SetActive(true);
                break;
            case "accent":
                if (string.Equals(Arg(0), "system", StringComparison.OrdinalIgnoreCase)) vm.UseSystemAccent = true;
                else
                {
                    vm.UseSystemAccent = false;
                    vm.AccentColorHex = Arg(0);
                }
                break;
            case "color":
                var key = Enum.Parse<CustomThemeColorKey>(Arg(0), true);
                var option = vm.BaseSurfaceOptions.Concat(vm.SurfaceOptions).Concat(vm.CalendarOptions).FirstOrDefault(candidate => candidate.Key == key)
                    ?? throw new ArgumentException($"No option {key}.");
                option.Value = Arg(1);
                break;
            case "expand":
                var expandKey = Enum.Parse<CustomThemeColorKey>(Arg(0), true);
                var row = _rows.FirstOrDefault(candidate => candidate.Key == expandKey) ?? throw new ArgumentException($"No row {expandKey}.");
                row.Host.SetExpanded(true);
                row.Row.SetExpanded(true);
                break;
            case "save":
                foreach (var well in AllWells()) well.Flush();
                if (vm.SaveCommand.CanExecute(null)) vm.SaveCommand.Execute(null);
                break;
            case "cancel":
                vm.CancelCommand.Execute(null);
                break;
            case "delete":
                if (vm.DeleteCommand.CanExecute(null)) vm.DeleteCommand.Execute(null);
                break;
            default:
                throw new ArgumentException($"Unknown debug-theme-editor operation '{operation}'.");
        }
        Serilog.Log.Information("Debug action: theme editor {Operation} {Arguments}.", operation, string.Join(' ', arguments));
    }

    internal string DebugState()
    {
        var vm = ViewModel;
        var expanded = _rows.Where(row => row.Preview is not null).Select(row => row.Key.ToString());
        var contrast = vm.BaseSurfaceOptions.Concat(vm.SurfaceOptions).Concat(vm.CalendarOptions)
            .Where(option => option.HasContrastReport).Select(option => $"{option.Key}:{option.ContrastMessage}");
        return $"editor name={vm.ThemeName} dirty={vm.IsDirty} dark={vm.IsDarkPalette} systemAccent={vm.UseSystemAccent} accent={vm.AccentColorHex} " +
               $"fit={vm.WallpaperFit} focal={vm.WallpaperAlignment} wallpaper={vm.HasWallpaper} file={_wallpaperFile} average={vm.WallpaperAverageColor} " +
               $"light*={vm.HasLightOverrides} dark*={vm.HasDarkOverrides} surfaces*={vm.OverriddenSurfaceCount} error={(vm.IsErrorOpen ? vm.ErrorMessage : string.Empty)} " +
               $"expanded={string.Join(',', expanded)} contrast={string.Join(';', contrast)} rows={_rows.Count}";
    }
}
#endif

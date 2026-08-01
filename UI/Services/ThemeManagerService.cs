using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.UI.Services;

/// <summary>
/// Сервис управления темами приложения.
/// Отвечает за загрузку, сохранение и применение тем.
/// </summary>
public sealed class ThemeManagerService
{
    private ThemeSettings? _cachedTheme;

    // PUBLIC API

    /// <summary>
    /// Загружает и применяет тему при старте приложения
    /// </summary>
    public void LoadAndApplyThemeOnStartup()
    {
        var theme = LoadThemeFromDisk();
        ApplyTheme(theme);
    }

    /// <summary>
    /// Применяет тему к ресурсам приложения
    /// </summary>
    public void ApplyTheme(ThemeSettings theme)
    {
        // ═══ ЗАЩИТА: проверка готовности Application ═══
        if (Application.Current?.Resources is not { } resources)
        {
            Log.Warn("Application.Current.Resources not available yet, deferring theme application");
            _cachedTheme = theme; // Сохраняем для повторной попытки
            return;
        }

        _cachedTheme = theme;

        // Backgrounds
        SetColor(resources, "BgPrimary", theme.BgPrimary);
        SetColor(resources, "BgSecondary", theme.BgSecondary);
        SetColor(resources, "BgElevated", theme.BgElevated);
        SetColor(resources, "BgHighlight", theme.BgHighlight);
        SetColor(resources, "BgHover", theme.BgHover);
        SetColor(resources, "BgSkeleton", theme.BgSkeleton);
        SetColor(resources, "BgSkeletonDeep", theme.BgSkeletonDeep);
        SetColor(resources, "BgOverlay", theme.BgOverlay);

        // Accent
        SetColor(resources, "Accent", theme.AccentColor);
        SetColor(resources, "AccentHover", theme.AccentHover);

        // Semantic
        SetColor(resources, "SystemError", theme.SystemError);
        SetColor(resources, "SystemErrorBg", theme.SystemErrorBg);
        SetColor(resources, "SystemInfoBlue", theme.SystemInfoBlue);
        SetColor(resources, "SystemWarnOrange", theme.SystemWarnOrange);

        // Text
        SetColor(resources, "TextPrimary", theme.TextPrimary);
        SetColor(resources, "TextSecondary", theme.TextSecondary);
        SetColor(resources, "TextMuted", theme.TextMuted);
        SetColor(resources, "TextDark", theme.TextDark);

        // ═══ КОНТРАСТНЫЙ ТЕКСТ ДЛЯ ACCENT КНОПОК ═══
        // Автоматически определяем чёрный или белый текст на акцентном фоне
        _ = TryParseColor(theme.AccentColor, out var accent);
        var accentButtonText = GetContrastingTextColor(accent);

        // Полный цвет
        resources["AccentButtonText"] = accentButtonText;
        resources["AccentButtonTextBrush"] = new SolidColorBrush(accentButtonText);

        // Прозрачная версия для плавных анимаций border (тот же RGB, альфа = 0)
        // Предотвращает белые вспышки при BrushTransition от/к "невидимому" состоянию
        var accentButtonTextTransparent = new Color(0, accentButtonText.R, accentButtonText.G, accentButtonText.B);
        resources["AccentButtonTextTransparent"] = accentButtonTextTransparent;
        resources["AccentButtonTextTransparentBrush"] = new SolidColorBrush(accentButtonTextTransparent);

        // ═══ AVALONIA FLUENT THEME COMPATIBILITY ═══
        // Переопределяем системные ресурсы для корректной работы стандартных контролов
        ApplyFluentOverrides(resources, theme);

        Log.Info($"Theme '{theme.Name}' applied.");
    }

    /// <summary>
    /// Вычисляет относительную яркость цвета по стандарту WCAG 2.0.
    /// Используется для расчёта контрастности.
    /// </summary>
    /// <param name="color">Цвет для анализа</param>
    /// <returns>Значение яркости от 0.0 (чёрный) до 1.0 (белый)</returns>
    private static double GetRelativeLuminance(Color color)
    {
        // Линеаризация sRGB канала по спецификации WCAG 2.0
        static double Linearize(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R)
             + 0.7152 * Linearize(color.G)
             + 0.0722 * Linearize(color.B);
    }

    /// <summary>
    /// Вычисляет WCAG 2.0 contrast ratio между двумя цветами.
    /// Результат от 1:1 (одинаковые) до 21:1 (чёрный/белый).
    /// </summary>
    /// <param name="color1">Первый цвет</param>
    /// <param name="color2">Второй цвет</param>
    /// <returns>Коэффициент контрастности (≥ 1.0)</returns>
    private static double GetContrastRatio(Color color1, Color color2)
    {
        var l1 = GetRelativeLuminance(color1);
        var l2 = GetRelativeLuminance(color2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Определяет контрастный цвет текста для данного фона.
    /// Использует WCAG 2.0 contrast ratio для выбора между чёрным и белым.
    /// Гарантирует читаемость текста на любом фоне, включая пограничные случаи
    /// (AMOLED Black с белым акцентом, Classic Green с зелёным акцентом).
    /// </summary>
    /// <param name="background">Цвет фона</param>
    /// <returns>Чёрный или белый цвет, обеспечивающий максимальный контраст</returns>
    private static Color GetContrastingTextColor(Color background)
    {
        var white = Color.FromRgb(255, 255, 255);
        var black = Color.FromRgb(0, 0, 0);

        var contrastWithWhite = GetContrastRatio(background, white);
        var contrastWithBlack = GetContrastRatio(background, black);

        // Выбираем цвет с бОльшим контрастом.
        // При равенстве предпочитаем белый (лучше читается на тёмных темах,
        // которые составляют большинство пресетов).
        return contrastWithBlack > contrastWithWhite ? black : white;
    }

    /// <summary>
    /// Применяет переопределения для Fluent темы Avalonia.
    /// Необходимо для корректной работы стандартных контролов (CheckBox, ToggleSwitch, 
    /// ComboBox, Slider, диалоговые окна и т.д.) с кастомными цветами темы.
    /// </summary>
    private static void ApplyFluentOverrides(IResourceDictionary resources, ThemeSettings theme)
    {
        _ = TryParseColor(theme.AccentColor, out var accent);
        _ = TryParseColor(theme.AccentHover, out var accentHover);
        _ = TryParseColor(theme.TextPrimary, out var textPrimary);
        _ = TryParseColor(theme.TextDark, out var textDark);
        _ = TryParseColor(theme.TextSecondary, out var textSecondary);
        _ = TryParseColor(theme.BgPrimary, out var bgPrimary);
        _ = TryParseColor(theme.BgSecondary, out var bgSecondary);
        _ = TryParseColor(theme.BgElevated, out var bgElevated);
        _ = TryParseColor(theme.BgHighlight, out var bgHighlight);
        _ = TryParseColor(theme.BgHover, out var bgHover);
        _ = TryParseColor(theme.TextMuted, out var textMuted);

        var accentButtonText = GetContrastingTextColor(accent);
        var transparent = Colors.Transparent;

        // ═══ SYSTEM ACCENT COLORS ═══
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorDark1"] = accentHover;
        resources["SystemAccentColorDark2"] = accentHover;
        resources["SystemAccentColorDark3"] = accentHover;
        resources["SystemAccentColorLight1"] = accentHover;
        resources["SystemAccentColorLight2"] = accentHover;
        resources["SystemAccentColorLight3"] = accentHover;

        // ═══ TEXT ON ACCENT ═══
        resources["TextOnAccentFillColorPrimary"] = accentButtonText;
        resources["TextOnAccentFillColorSecondary"] = accentButtonText;
        resources["TextOnAccentFillColorDisabled"] = textSecondary;
        resources["TextOnAccentFillColorSelectedText"] = accentButtonText;

        // ═══ GENERAL TEXT ═══
        resources["TextFillColorPrimary"] = textPrimary;
        resources["TextFillColorSecondary"] = textSecondary;
        resources["TextFillColorTertiary"] = textSecondary;
        resources["TextFillColorDisabled"] = textSecondary;
        resources["TextFillColorInverse"] = textDark;

        // ═══ CONTROL BACKGROUNDS ═══
        resources["ControlFillColorDefault"] = bgElevated;
        resources["ControlFillColorSecondary"] = bgElevated;
        resources["ControlFillColorTertiary"] = bgHighlight;
        resources["ControlFillColorInputActive"] = bgHover;
        resources["ControlFillColorDisabled"] = bgHighlight;

        resources["ControlStrokeColorDefault"] = bgHighlight;
        resources["ControlStrokeColorSecondary"] = bgHighlight;
        resources["ControlStrongStrokeColorDefault"] = textSecondary;
        resources["ControlStrongStrokeColorDisabled"] = bgHighlight;

        // ═══ SUBTLE FILLS ═══
        resources["SubtleFillColorTransparent"] = transparent;
        resources["SubtleFillColorSecondary"] = bgHover;
        resources["SubtleFillColorTertiary"] = bgHighlight;
        resources["SubtleFillColorDisabled"] = transparent;

        // ═══ ACCENT FILLS ═══
        resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        resources["AccentFillColorSecondary"] = accentHover;
        resources["AccentFillColorTertiary"] = accent;
        resources["AccentFillColorDisabled"] = bgHighlight;

        // ═══ ACCENT BUTTON ═══
        resources["AccentButtonBackground"] = accent;
        resources["AccentButtonBackgroundPointerOver"] = accent;
        resources["AccentButtonBackgroundPressed"] = accentHover;
        resources["AccentButtonBackgroundDisabled"] = bgHighlight;

        resources["AccentButtonForeground"] = accentButtonText;
        resources["AccentButtonForegroundPointerOver"] = accentButtonText;
        resources["AccentButtonForegroundPressed"] = accentButtonText;
        resources["AccentButtonForegroundDisabled"] = textSecondary;

        resources["AccentButtonBorderBrush"] = transparent;
        resources["AccentButtonBorderBrushPointerOver"] = accentButtonText;
        resources["AccentButtonBorderBrushPressed"] = transparent;
        resources["AccentButtonBorderBrushDisabled"] = transparent;

        // ═══ CHECKBOX ═══
        resources["CheckBoxCheckBackgroundFillUnchecked"] = bgElevated;
        resources["CheckBoxCheckBackgroundFillUncheckedPointerOver"] = bgElevated;
        resources["CheckBoxCheckBackgroundFillUncheckedPressed"] = bgElevated;
        resources["CheckBoxCheckBackgroundFillUncheckedDisabled"] = bgHighlight;
        resources["CheckBoxCheckBackgroundFillChecked"] = accent;
        resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = accent;
        resources["CheckBoxCheckBackgroundFillCheckedPressed"] = accentHover;
        resources["CheckBoxCheckBackgroundFillCheckedDisabled"] = bgHighlight;

        resources["CheckBoxCheckBackgroundStrokeUnchecked"] = bgHighlight;
        resources["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = accent;
        resources["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = accent;
        resources["CheckBoxCheckBackgroundStrokeUncheckedDisabled"] = bgHighlight;
        resources["CheckBoxCheckBackgroundStrokeChecked"] = accent;
        resources["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = accentHover;
        resources["CheckBoxCheckBackgroundStrokeCheckedPressed"] = accent;
        resources["CheckBoxCheckBackgroundStrokeCheckedDisabled"] = bgHighlight;

        resources["CheckBoxCheckGlyphForegroundUnchecked"] = transparent;
        resources["CheckBoxCheckGlyphForegroundUncheckedPointerOver"] = transparent;
        resources["CheckBoxCheckGlyphForegroundUncheckedPressed"] = transparent;
        resources["CheckBoxCheckGlyphForegroundUncheckedDisabled"] = transparent;
        resources["CheckBoxCheckGlyphForegroundChecked"] = accentButtonText;
        resources["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = accentButtonText;
        resources["CheckBoxCheckGlyphForegroundCheckedPressed"] = accentButtonText;
        resources["CheckBoxCheckGlyphForegroundCheckedDisabled"] = textSecondary;

        // ═══ TOGGLESWITCH ═══
        resources["ToggleSwitchContainerBackground"] = transparent;
        resources["ToggleSwitchContainerBackgroundPointerOver"] = transparent;

        resources["ToggleSwitchFillOff"] = bgHighlight;
        resources["ToggleSwitchFillOffPointerOver"] = bgHighlight;
        resources["ToggleSwitchFillOffPressed"] = bgHighlight;
        resources["ToggleSwitchFillOffDisabled"] = bgHighlight;

        resources["ToggleSwitchFillOn"] = accent;
        resources["ToggleSwitchFillOnPointerOver"] = accent;
        resources["ToggleSwitchFillOnPressed"] = accentHover;
        resources["ToggleSwitchFillOnDisabled"] = bgHighlight;

        resources["ToggleSwitchStrokeOff"] = bgHighlight;
        resources["ToggleSwitchStrokeOffPointerOver"] = accent;
        resources["ToggleSwitchStrokeOffPressed"] = accent;
        resources["ToggleSwitchStrokeOffDisabled"] = bgHighlight;

        resources["ToggleSwitchStrokeOn"] = accent;
        resources["ToggleSwitchStrokeOnPointerOver"] = accentHover;
        resources["ToggleSwitchStrokeOnPressed"] = accent;
        resources["ToggleSwitchStrokeOnDisabled"] = bgHighlight;

        resources["ToggleSwitchKnobFillOff"] = textSecondary;
        resources["ToggleSwitchKnobFillOffPointerOver"] = textPrimary;
        resources["ToggleSwitchKnobFillOffPressed"] = textPrimary;
        resources["ToggleSwitchKnobFillOffDisabled"] = textMuted;

        resources["ToggleSwitchKnobFillOn"] = accentButtonText;
        resources["ToggleSwitchKnobFillOnPointerOver"] = accentButtonText;
        resources["ToggleSwitchKnobFillOnPressed"] = accentButtonText;
        resources["ToggleSwitchKnobFillOnDisabled"] = textMuted;

        // ═══ SLIDER ═══
        resources["SliderTrackFill"] = bgHighlight;
        resources["SliderTrackFillPointerOver"] = bgHighlight;
        resources["SliderTrackFillPressed"] = bgHighlight;
        resources["SliderTrackFillDisabled"] = bgHighlight;

        resources["SliderTrackValueFill"] = accent;
        resources["SliderTrackValueFillPointerOver"] = accentHover;
        resources["SliderTrackValueFillPressed"] = accent;
        resources["SliderTrackValueFillDisabled"] = bgHighlight;

        resources["SliderThumbBackground"] = textPrimary;
        resources["SliderThumbBackgroundPointerOver"] = accent;
        resources["SliderThumbBackgroundPressed"] = accentHover;
        resources["SliderThumbBackgroundDisabled"] = textSecondary;

        // ═══ COMBOBOX ═══
        resources["ComboBoxBackground"] = bgElevated;
        resources["ComboBoxBackgroundPointerOver"] = bgElevated;
        resources["ComboBoxBackgroundPressed"] = bgElevated;
        resources["ComboBoxBackgroundDisabled"] = bgHighlight;

        resources["ComboBoxBorderBrush"] = bgHighlight;
        resources["ComboBoxBorderBrushPointerOver"] = accent;
        resources["ComboBoxBorderBrushPressed"] = accent;
        resources["ComboBoxBorderBrushDisabled"] = bgHighlight;

        resources["ComboBoxForeground"] = textPrimary;
        resources["ComboBoxForegroundPointerOver"] = textPrimary;
        resources["ComboBoxForegroundPressed"] = textPrimary;
        resources["ComboBoxForegroundDisabled"] = textSecondary;

        resources["ComboBoxDropDownBackground"] = bgElevated;
        resources["ComboBoxDropDownBorderBrush"] = bgHighlight;

        resources["ComboBoxItemBackground"] = transparent;
        resources["ComboBoxItemBackgroundPointerOver"] = transparent;
        resources["ComboBoxItemBackgroundPressed"] = transparent;
        resources["ComboBoxItemBackgroundDisabled"] = transparent;
        resources["ComboBoxItemBackgroundSelected"] = accent;
        resources["ComboBoxItemBackgroundSelectedPointerOver"] = accentHover;
        resources["ComboBoxItemBackgroundSelectedPressed"] = accent;

        resources["ComboBoxItemForeground"] = textPrimary;
        resources["ComboBoxItemForegroundPointerOver"] = textPrimary;
        resources["ComboBoxItemForegroundPressed"] = textPrimary;
        resources["ComboBoxItemForegroundDisabled"] = textSecondary;
        resources["ComboBoxItemForegroundSelected"] = accentButtonText;
        resources["ComboBoxItemForegroundSelectedPointerOver"] = accentButtonText;
        resources["ComboBoxItemForegroundSelectedPressed"] = accentButtonText;

        // ═══ LISTBOX ═══
        resources["ListBoxBackground"] = transparent;
        resources["ListBoxBorderBrush"] = transparent;

        resources["ListBoxItemBackground"] = transparent;
        resources["ListBoxItemBackgroundPointerOver"] = transparent;
        resources["ListBoxItemBackgroundPressed"] = transparent;
        resources["ListBoxItemBackgroundDisabled"] = transparent;
        resources["ListBoxItemBackgroundSelected"] = accent;
        resources["ListBoxItemBackgroundSelectedPointerOver"] = accentHover;
        resources["ListBoxItemBackgroundSelectedPressed"] = accent;
        resources["ListBoxItemBackgroundSelectedDisabled"] = bgHighlight;

        resources["ListBoxItemForeground"] = textPrimary;
        resources["ListBoxItemForegroundPointerOver"] = textPrimary;
        resources["ListBoxItemForegroundPressed"] = textPrimary;
        resources["ListBoxItemForegroundDisabled"] = textSecondary;
        resources["ListBoxItemForegroundSelected"] = accentButtonText;
        resources["ListBoxItemForegroundSelectedPointerOver"] = accentButtonText;
        resources["ListBoxItemForegroundSelectedPressed"] = accentButtonText;
        resources["ListBoxItemForegroundSelectedDisabled"] = textSecondary;

        // ═══ RADIOBUTTON ═══
        resources["RadioButtonBackground"] = transparent;
        resources["RadioButtonBackgroundPointerOver"] = transparent;
        resources["RadioButtonBackgroundPressed"] = transparent;
        resources["RadioButtonBackgroundDisabled"] = transparent;

        resources["RadioButtonForeground"] = textPrimary;
        resources["RadioButtonForegroundPointerOver"] = textPrimary;
        resources["RadioButtonForegroundPressed"] = textPrimary;
        resources["RadioButtonForegroundDisabled"] = textSecondary;

        resources["RadioButtonOuterEllipseFill"] = bgElevated;
        resources["RadioButtonOuterEllipseFillPointerOver"] = bgElevated;
        resources["RadioButtonOuterEllipseFillPressed"] = bgElevated;
        resources["RadioButtonOuterEllipseFillDisabled"] = bgHighlight;

        resources["RadioButtonOuterEllipseStroke"] = bgHighlight;
        resources["RadioButtonOuterEllipseStrokePointerOver"] = accent;
        resources["RadioButtonOuterEllipseStrokePressed"] = accent;
        resources["RadioButtonOuterEllipseStrokeDisabled"] = bgHighlight;

        resources["RadioButtonOuterEllipseCheckedFill"] = bgElevated;
        resources["RadioButtonOuterEllipseCheckedFillPointerOver"] = bgElevated;
        resources["RadioButtonOuterEllipseCheckedFillPressed"] = bgElevated;
        resources["RadioButtonOuterEllipseCheckedFillDisabled"] = bgHighlight;

        resources["RadioButtonOuterEllipseCheckedStroke"] = accent;
        resources["RadioButtonOuterEllipseCheckedStrokePointerOver"] = accentHover;
        resources["RadioButtonOuterEllipseCheckedStrokePressed"] = accent;
        resources["RadioButtonOuterEllipseCheckedStrokeDisabled"] = bgHighlight;

        resources["RadioButtonCheckGlyphFill"] = accent;
        resources["RadioButtonCheckGlyphFillPointerOver"] = accentHover;
        resources["RadioButtonCheckGlyphFillPressed"] = accent;
        resources["RadioButtonCheckGlyphFillDisabled"] = textMuted;

        // ═══ BACKGROUNDS ═══
        resources["SolidBackgroundFillColorBase"] = bgPrimary;
        resources["SolidBackgroundFillColorSecondary"] = bgSecondary;
        resources["SolidBackgroundFillColorTertiary"] = bgElevated;
        resources["SolidBackgroundFillColorQuarternary"] = bgHighlight;

        resources["LayerFillColorDefault"] = bgElevated;
        resources["LayerFillColorAlt"] = bgSecondary;
        resources["LayerOnMicaBaseAltFillColorDefault"] = bgPrimary;

        resources["CardBackgroundFillColorDefault"] = bgElevated;
        resources["CardBackgroundFillColorSecondary"] = bgSecondary;
        resources["CardStrokeColorDefault"] = bgHighlight;

        resources["DividerStrokeColorDefault"] = bgHighlight;

        // ═══ FLYOUT / POPUP ═══
        resources["FlyoutBackground"] = bgElevated;
        resources["FlyoutBorderThemeBrush"] = new SolidColorBrush(bgHighlight);

        // ═══ MENU ═══
        resources["MenuFlyoutPresenterBackground"] = bgElevated;
        resources["MenuFlyoutPresenterBorderBrush"] = new SolidColorBrush(bgHighlight);
        resources["MenuFlyoutItemBackground"] = transparent;
        resources["MenuFlyoutItemBackgroundPointerOver"] = bgHighlight;
        resources["MenuFlyoutItemForeground"] = textPrimary;
        resources["MenuFlyoutItemForegroundPointerOver"] = textPrimary;

        // ═══ BUTTON ═══
        resources["ButtonBackground"] = bgElevated;
        resources["ButtonBackgroundPointerOver"] = bgElevated;
        resources["ButtonBackgroundPressed"] = bgHighlight;
        resources["ButtonBackgroundDisabled"] = bgHighlight;

        resources["ButtonForeground"] = textPrimary;
        resources["ButtonForegroundPointerOver"] = textPrimary;
        resources["ButtonForegroundPressed"] = textPrimary;
        resources["ButtonForegroundDisabled"] = textSecondary;

        resources["ButtonBorderBrush"] = bgHighlight;
        resources["ButtonBorderBrushPointerOver"] = accent;
        resources["ButtonBorderBrushPressed"] = accentHover;
        resources["ButtonBorderBrushDisabled"] = bgHighlight;

        // ═══ CONTENT DIALOG ═══
        resources["ContentDialogBackground"] = bgElevated;
        resources["ContentDialogTopOverlay"] = bgElevated;
        resources["ContentDialogBorderBrush"] = bgHighlight;
        resources["ContentDialogForeground"] = textPrimary;

        // ═══ INFOBADGE / NOTIFICATIONS ═══
        resources["InfoBadgeForeground"] = textDark;
        resources["InfoBarErrorSeverityIconBackground"] = textDark;
        resources["InfoBarWarningSeverityIconBackground"] = textDark;
        resources["InfoBarSuccessSeverityIconBackground"] = textDark;
        resources["InfoBarInformationalSeverityIconBackground"] = textDark;

        // ═══ FOCUS VISUAL ═══
        resources["FocusVisualPrimaryBrush"] = new SolidColorBrush(accent);
        resources["FocusVisualSecondaryBrush"] = new SolidColorBrush(transparent);
        resources["SystemControlFocusVisualPrimaryBrush"] = new SolidColorBrush(accent) { Opacity = 0.85 };
        resources["SystemControlFocusVisualSecondaryBrush"] = new SolidColorBrush(transparent);

        // ═══ MENU ITEM — убиваем фоновую заливку Fluent ═══
        resources["MenuFlyoutItemBackground"] = transparent;
        resources["MenuFlyoutItemBackgroundPointerOver"] = transparent;
        resources["MenuFlyoutItemBackgroundPressed"] = transparent;
        resources["MenuFlyoutItemBackgroundDisabled"] = transparent;
        resources["MenuFlyoutSubItemBackground"] = transparent;
        resources["MenuFlyoutSubItemBackgroundPointerOver"] = transparent;
        resources["MenuFlyoutSubItemBackgroundPressed"] = transparent;
        resources["MenuFlyoutSubItemBackgroundDisabled"] = transparent;
    }

    /// <summary>
    /// Сохраняет тему на диск
    /// </summary>
    public void SaveTheme(ThemeSettings theme)
    {
        try
        {
            var json = JsonSerializer.Serialize(theme, AppJsonContext.Default.ThemeSettings);
            File.WriteAllText(G.FilePath.Theme, json);
            _cachedTheme = theme;
            Log.Info($"Theme '{theme.Name}' saved.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save theme: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает текущую загруженную тему
    /// </summary>
    public ThemeSettings GetCurrentTheme()
    {
        return _cachedTheme ?? LoadThemeFromDisk();
    }

    /// <summary>
    /// Возвращает дефолтную тему (Paralax Purple)
    /// </summary>
    public static ThemeSettings GetDefaultTheme() => new() { IsBuiltIn = true };

    /// <summary>
    /// Сбрасывает тему к дефолтной
    /// </summary>
    public void ResetToDefault()
    {
        try
        {
            if (File.Exists(G.FilePath.Theme))
                File.Delete(G.FilePath.Theme);
        }
        catch { /* Игнорируем ошибку удаления */ }

        var def = GetDefaultTheme();
        SaveTheme(def);
        ApplyTheme(def);
    }

    /// <summary>
    /// Возвращает список встроенных пресетов тем
    /// </summary>
    public static IReadOnlyList<ThemeSettings> GetBuiltInPresets() =>
    [
        // ═══ 1. PARALAX PURPLE (Default) ═══
        new ThemeSettings { IsBuiltIn = true },

        // ═══ 2. CLASSIC GREEN (Spotify-like) ═══
        new ThemeSettings
        {
            Name = "Classic Green",
            IsBuiltIn = true,
            BgPrimary = "#121212",
            BgSecondary = "#1E1E1E",
            BgElevated = "#282828",
            BgHighlight = "#404040",
            BgHover = "#505050",
            BgSkeleton = "#282828",
            BgSkeletonDeep = "#1a1a1a",
            BgOverlay = "#CC121212",
            AccentColor = "#1DB954",
            AccentHover = "#20e063",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#B3B3B3",
            TextMuted = "#888888",
            TextDark = "#000000"
        },

        // ═══ 3. OCEAN DEEP ═══
        new ThemeSettings
        {
            Name = "Ocean Deep",
            IsBuiltIn = true,
            BgPrimary = "#001219",
            BgSecondary = "#001f2d",
            BgElevated = "#002d42",
            BgHighlight = "#003b57",
            BgHover = "#00506b",
            BgSkeleton = "#002535",
            BgSkeletonDeep = "#000d12",
            BgOverlay = "#CC001219",
            AccentColor = "#00B4D8",
            AccentHover = "#45c9e4",
            TextPrimary = "#E0FBFC",
            TextSecondary = "#98C1D9",
            TextMuted = "#5B8FA8",
            TextDark = "#001219"
        },

        // ═══ 4. AMOLED BLACK ═══
        new ThemeSettings
        {
            Name = "AMOLED Black",
            IsBuiltIn = true,
            BgPrimary = "#000000",
            BgSecondary = "#0A0A0A",
            BgElevated = "#141414",
            BgHighlight = "#1F1F1F",
            BgHover = "#2A2A2A",
            BgSkeleton = "#141414",
            BgSkeletonDeep = "#050505",
            BgOverlay = "#CC000000",
            AccentColor = "#FFFFFF",
            AccentHover = "#CCCCCC",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#A0A0A0",
            TextMuted = "#606060",
            TextDark = "#000000"
        },

        // ═══ 5. WARM SUNSET ═══
        new ThemeSettings
        {
            Name = "Warm Sunset",
            IsBuiltIn = true,
            BgPrimary = "#1A1210",
            BgSecondary = "#261A16",
            BgElevated = "#33221C",
            BgHighlight = "#4A3228",
            BgHover = "#5C3F32",
            BgSkeleton = "#2A1C18",
            BgSkeletonDeep = "#120E0C",
            BgOverlay = "#CC1A1210",
            AccentColor = "#FF6B35",
            AccentHover = "#FF8C5A",
            TextPrimary = "#FFF5F0",
            TextSecondary = "#D4B5A5",
            TextMuted = "#8B7265",
            TextDark = "#1A1210"
        },

        // ═══ 6. DRACULA ═══
        new ThemeSettings
        {
            Name = "Dracula",
            IsBuiltIn = true,
            BgPrimary = "#282a36",
            BgSecondary = "#21222c",
            BgElevated = "#343746",
            BgHighlight = "#44475a",
            BgHover = "#4d5066",
            BgSkeleton = "#343746",
            BgSkeletonDeep = "#1e1f29",
            BgOverlay = "#CC282a36",
            AccentColor = "#bd93f9",
            AccentHover = "#d4b8ff",
            TextPrimary = "#f8f8f2",
            TextSecondary = "#bfbfbf",
            TextMuted = "#6272a4",
            TextDark = "#282a36"
        }
    ];

    // PRIVATE HELPERS

    private ThemeSettings LoadThemeFromDisk()
    {
        try
        {
            if (File.Exists(G.FilePath.Theme))
            {
                var json = File.ReadAllText(G.FilePath.Theme);
                var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.ThemeSettings);
                if (theme != null)
                {
                    _cachedTheme = theme;
                    return theme;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load theme: {ex.Message}");
        }

        // Сохраняем дефолтную при первом запуске
        var def = GetDefaultTheme();
        SaveTheme(def);
        return def;
    }

    private static void SetColor(IResourceDictionary resources, string key, string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            Log.Error($"Invalid color: {key}={hex}");
            color = Colors.Magenta;
        }

        resources[key] = color;
        resources[$"{key}Brush"] = new SolidColorBrush(color);

        // Прозрачная версия для анимаций (альфа = 0, но тот же RGB)
        var transparent = new Color(0, color.R, color.G, color.B);
        resources[$"{key}Transparent"] = transparent;
        resources[$"{key}TransparentBrush"] = new SolidColorBrush(transparent);
    }

    /// <summary>
    /// Читает цвет из словаря ресурсов активной темы Avalonia.
    /// При отсутствии ключа возвращает серый как безопасный fallback.
    /// </summary>
    public static Color GetThemeColor(string key)
    {
        if (Application.Current?.Resources
                .TryGetResource(key, null, out var res) == true
            && res is Color color)
            return color;

        return Colors.Gray;
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
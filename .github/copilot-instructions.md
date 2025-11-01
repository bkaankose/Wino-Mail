# Copilot Instructions for Wino-Mail Project

## ViewModels Development Guidelines

### Observable Properties
- **ALWAYS** use `public partial` observable properties in ViewModels
- **NEVER** use private fields with `[ObservableProperty]` attribute
- **Correct Pattern:**
  ```csharp
  [ObservableProperty]
  public partial string SearchQuery { get; set; } = string.Empty;
  ```
- **Incorrect Pattern:**
  ```csharp
  [ObservableProperty]
  private string searchQuery = string.Empty;
  ```

### ViewModels Structure
- All ViewModels should inherit from appropriate base classes (MailBaseViewModel, CoreBaseViewModel, etc.)
- Use `[RelayCommand]` for command methods
- Follow the existing dependency injection patterns
- Use `IMailDialogService` for Mail-related dialogs, `IDialogServiceBase` for core dialogs

### Strings and Localization
- All strings should be added in English to en-US.json file and used via Translator class.
- Avoid hardcoding strings in the codebase.
- Use Translator.{TranslationKey} with property to retrieve string in view models.
- Use Translator as well in the XAML files with x:Bind. Always use OneTime mode.

### Dialog Services
- Use `InfoBarMessage()` method for simple notifications
- Use `ShowConfirmationDialogAsync()` for confirmation dialogs
- Use `PickFilesAsync()` for file selection
- Always check for null/empty results from dialog operations

### File Structure
- Place ViewModels in appropriate projects (Wino.Mail.ViewModels, Wino.Core.ViewModels, etc.)
- Create abstract page classes in Views/Abstract folders
- Follow the existing naming conventions
- **NEVER** edit files in the old Wino.Mail project folder - it's UWP and deprecated
- **ALWAYS** work with WinUI projects (Wino.Mail.WinUI, Wino.Core.WinUI) for UI components

### Error Handling
- Always wrap async operations in try-catch blocks
- Use InfoBar messages for user-friendly error notifications
- Log errors appropriately but don't expose technical details to users

### Collection Management
- Use ObservableCollection for UI-bound collections
- Implement proper filtering and search functionality
- Use virtualization for large datasets (ListView, ItemsView)

### Contact Management Specific
- Respect IsRootContact property - root contacts cannot be deleted
- Check IsOverridden property during synchronization
- Always validate contact data before operations
- Use PersonPicture controls for contact avatars
- Support multiple selection where appropriate

### UI Data Binding and Converters
- **NEVER** create IValueConverter classes or add them to Converters.xaml
- **NEVER** use BoolToVisibilityConverter - boolean values are automatically converted to Visibility by the WinUI 3 SDK
- **ALWAYS** use XamlHelpers static methods for data transformations when needed
- Use XamlHelpers methods with x:Bind syntax: `{x:Bind helpers:XamlHelpers.ReverseBoolToVisibilityConverter(PropertyName), Mode=OneWay}`
- Available XamlHelpers methods include: ReverseBoolToVisibilityConverter, CountToBooleanConverter, BoolToSelectionMode, Base64ToBitmapImage, etc.
- If a helper method doesn't exist, add it to XamlHelpers.cs instead of creating a converter
- For boolean to Visibility binding, use direct binding: `Visibility="{x:Bind BooleanProperty, Mode=OneWay}"`

## Code Style
- Use var where type is obvious
- Use string interpolation over string.Format where simple
- Keep methods focused and single-responsibility
- Use meaningful variable names
- Add XML documentation for public APIs

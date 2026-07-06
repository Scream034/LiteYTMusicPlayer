// ==========================================
//           ГЛОБАЛЬНЫЕ ИМПОРТЫ LMP
// ==========================================

// Системные пространства
global using System;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Reactive.Disposables.Fluent;

global using ReactiveUI.SourceGenerators;

// Пространства имен Ядра (LMP.Core)
global using LMP.Core.Models;
global using LMP.Core.Services;
global using LMP.Core.ViewModels;
global using LMP.Core.Helpers;
global using LMP.Core.Audio;
global using Log = LMP.Core.Logger.Log;

// Пространства имен UI (LMP)
global using LMP.UI.Models;
global using LMP.UI.Services;
global using LMP.UI.Helpers;
global using LMP.UI.ViewModels;

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LMP.Core")]
[assembly: InternalsVisibleTo("LMP.Tests")]
# Сторонние компоненты Luma Island 0.7.0

Исходный код Luma Island распространяется под [MIT](../LICENSE). Самодостаточная переносимая сборка Windows x64 также содержит компоненты Microsoft с собственными лицензиями. Лицензия Luma не заменяет условия этих компонентов.

Упаковщик сверяет runtime-пакеты с [проверенным перечнем](licenses/dependencies.json) и останавливается при изменении их версий. Перед обновлением перечня нужно проверить и обновить лицензии.

Перечень ниже — снимок зависимостей переносимого релиза 0.7.0. Версии runtime-пакетов не изменились после проверки комплекта 0.5.2 и сверяются упаковщиком с фактическим файлом зависимостей приложения. Файлы SDK, компиляторы, анализаторы и инструменты сборки в состав приложения не включаются.

| Компонент в сборке | Происхождение | Условия и сохранённые уведомления |
|---|---|---|
| .NET runtime и базовые библиотеки | `Microsoft.NETCore.App.Runtime.win-x64` **10.0.14** | MIT согласно `.nuspec` и `LICENSE.TXT` именно этого runtime-пакета. [Лицензия](licenses/dotnet-runtime-10.0.14-LICENSE.txt), [уведомления о сторонних компонентах](licenses/dotnet-runtime-10.0.14-THIRD-PARTY-NOTICES.txt). [Пакет Microsoft](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.14). |
| WPF и библиотеки Windows Desktop | `Microsoft.WindowsDesktop.App.Runtime.win-x64` **10.0.14** | MIT согласно `.nuspec` и `LICENSE` данного пакета. [Лицензия](licenses/dotnet-windowsdesktop-10.0.14-LICENSE.txt). Отдельного файла third-party notices в этом пакете нет. [Пакет Microsoft](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.14). |
| `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll` | `Microsoft.Windows.SDK.NET.Ref` **10.0.19041.57**, каталог `lib/net8.0` | Пакет указывает [Microsoft Windows SDK License](https://aka.ms/WinSDKLicenseURL), а не выражение MIT. Сохранены [официальный оригинал RTF](licenses/windows-sdk-LICENSE.rtf) и [читаемая текстовая копия](licenses/windows-sdk-LICENSE.txt). Неизменённые DLL прямо перечислены в [разрешённом к распространению коде Windows SDK](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref). [Пакет Microsoft](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.57). |
| C#/WinRT runtime, реализованный в `WinRT.Runtime.dll` | Версия файла **2.2.0.48161**, исходный commit `8649ee3eeb2445ca2a36d80d878ef60b96a6c65d` | Дополнительно сохранена MIT-лицензия исходного проекта C#/WinRT: [локальная копия](licenses/cswinrt-2.2.0-LICENSE.txt), [первичный источник Microsoft](https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE). Это уведомление не подменяет лицензию распространяемого Windows SDK NuGet-пакета. |

## Происхождение файлов лицензий

Три файла `dotnet-*.txt` скопированы без изменения байтов непосредственно из указанных runtime-пакетов NuGet. Сводный `THIRD-PARTY-NOTICES` .NET сохранён полностью: он содержит уведомления и условия встроенных сторонних компонентов, которые могут отличаться от MIT.

Лицензия установленного инструментария .NET SDK не использовалась в качестве лицензии runtime. Для этой сборки источником являются лицензии и метаданные конкретных пакетов версии 10.0.14; общие сведения о лицензировании других версий .NET не заменяют эти файлы.

Пакет Windows SDK не содержит самостоятельного файла лицензии: его `.nuspec` ссылается на `https://aka.ms/WinSDKLicenseURL`. Оригинал RTF получен по этой ссылке с сайта Microsoft; TXT — преобразование того же текста для удобства чтения. В случае расхождения используется оригинал. Контрольные суммы обеих распространяемых SDK DLL совпали с файлами исходного NuGet-пакета.

Версия `Microsoft.Windows.SDK.NET.dll` в этом пакете — **10.0.19041.55**; она отличается от версии пакета **10.0.19041.57**. Это исходная комплектация Microsoft, а не изменение DLL в Luma.

## Необязательный NVIDIA FrameView SDK

Интеграция FPS обращается к отдельно установленному NVIDIA FrameView SDK. DLL, исполняемые файлы помощников, служба, заголовки, статические библиотеки и исходники SDK не входят в дистрибутив Luma. Его условия не заменяются лицензией MIT Luma, а лицензия SDK не включается в этот комплект. Сведения о продукте: [NVIDIA FrameView](https://www.nvidia.com/en-us/geforce/technologies/frameview/).

## Комплект переносимого релиза

Для температуры GPU Luma динамически обращается к NVML из уже установленного драйвера NVIDIA. NVML, NVAPI, драйверы, заголовки и SDK NVIDIA в исходники и архив Luma не включаются. Эта интеграция не меняет лицензию установленного драйвера; [документация NVML](https://docs.nvidia.com/deploy/nvml-api/latest/index.html).

Вместе с бинарными файлами сохраняйте корневую лицензию Luma, этот документ и весь каталог `docs/licenses/`, сохранив их относительное расположение.

Patch-версия runtime в проекте не закреплена: повторная сборка с другой версией .NET SDK 10.x, в том числе в CI, может разрешить другой runtime-пакет даже при той же версии Luma. Перед упаковкой такой сборки нужно сверить фактические пакеты из `project.assets.json`, версии выходных DLL, лицензии и уведомления; затем обновить этот перечень и копии соответствующих файлов. Упаковщик останавливает сборку архива при несовпадении перечня runtime-пакетов.

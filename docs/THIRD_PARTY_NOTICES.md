# Сторонние компоненты Luma Island 0.9.0

Собственный исходный код Luma Island и модуля датчиков распространяется под [MIT](../LICENSE). Переносимая сборка Windows x64 также содержит сторонние компоненты с собственными лицензиями. Лицензия Luma не заменяет их условия.

Упаковщик сверяет runtime-пакеты с [проверенным перечнем](licenses/dependencies.json) и останавливается при изменении их версий. Перед обновлением перечня нужно проверить и обновить лицензии.

Перечень ниже — снимок зависимостей переносимого релиза 0.9.0. Версии пакетов не изменились после 0.8.0; упаковщик сверяет их с фактическими зависимостями. Каталоги установленного .NET SDK, компиляторы и анализаторы Luma в runtime не копируются. Для встроенных модулей PawnIO отдельно приложен неизменённый архив соответствующих исходников с upstream-файлами сборки.

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

## Необязательный модуль датчиков CPU / RAM

Каталог `sensors/` содержит отдельную самодостаточную службу и перечисленные ниже зависимости. Она не загружается в процесс оверлея. Установка службы выполняется только явным действием пользователя. Драйвер PawnIO в дистрибутив не входит.

Фактический перечень пакетов сверяется с [sensors-dependencies.json](licenses/sensors-dependencies.json). [Подробный манифест](licenses/sensors/manifest.json) содержит происхождение, версии, хеши распространяемых DLL и сохранённых документов. Наличие пакета при восстановлении проекта не означает, что его DLL вошла в Windows-архив: манифест различает эти случаи.

| Компонент | Версия | Лицензии и уведомления |
|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | [MPL 2.0](licenses/sensors/librehardwaremonitor-0.9.6-LICENSE.txt), [полные сторонние уведомления](licenses/sensors/librehardwaremonitor-0.9.6-THIRD-PARTY-NOTICES.txt). [Исходники версии](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6). |
| RAMSPDToolkit-NDD | 1.4.2 | [MPL 2.0](licenses/sensors/ramspdtoolkit-ndd-1.4.2-LICENSE.txt). Точное происхождение и commit пакета сохранены в манифесте. |
| BlackSharp.Core | 1.0.7 | [MPL 2.0](licenses/sensors/blacksharp-core-1.0.7-LICENSE.txt). |
| DiskInfoToolkit | 1.1.2 | [MPL 2.0](licenses/sensors/diskinfotoolkit-1.1.2-LICENSE.txt), [сторонние уведомления](licenses/sensors/diskinfotoolkit-1.1.2-THIRD-PARTY-NOTICES.txt). Сканирование накопителей в модуле Luma выключено. |
| HidSharp | 2.6.4 | [Apache 2.0 и оригинальное уведомление автора](licenses/sensors/hidsharp-2.6.4-LICENSE.txt). Контроллеры HID модуль Luma не опрашивает. |
| Mono.Posix.NETStandard | 1.0.0 | [Оригинальная лицензия и сторонние условия](licenses/sensors/mono-Posix-NETStandard-1.0.0-LICENSE.txt), [PATENTS](licenses/sensors/mono-PATENTS.txt). Уведомления сохранены целиком. |
| System.CodeDom / System.Management | 10.0.2 | [MIT](licenses/sensors/dotnet-system-LICENSE.txt), [сторонние уведомления .NET](licenses/sensors/dotnet-system-THIRD-PARTY-NOTICES.txt). |
| System.IO.Ports | 10.0.3 | Те же [MIT](licenses/sensors/dotnet-system-LICENSE.txt) и [уведомления .NET](licenses/sensors/dotnet-system-THIRD-PARTY-NOTICES.txt). |
| .NET Core runtime Windows x64 | 10.0.14 | Копия runtime службы имеет те же MIT и полные уведомления runtime, что и основной процесс в таблице выше. |

LHM содержит готовые модули **PawnIO.Modules 0.2.2** под **LGPL-2.1-or-later**. Они являются отдельными сторонними компонентами; MPL LHM и MIT Luma не подменяют их лицензию. Сохранены [COPYING](licenses/sensors/pawnio-modules-0.2.2-COPYING.txt) и [полный неизменённый архив соответствующих исходников](licenses/sensors/pawnio-modules-0.2.2-source.tar.gz) из commit `e12a858d952461ee2e919897cacbff7f905fe370`. SHA-256 архива: `e82ef4c5c06461c74685fa60ebf28297e4830876f8bbf388187d7aa063bcf096`. Архив включает upstream-файлы и инструменты сборки; они не выполняются Luma. [Первичный источник](https://github.com/namazso/PawnIO.Modules/tree/e12a858d952461ee2e919897cacbff7f905fe370).

Библиотеки распространяются отдельными неизменёнными DLL, а исходники Luma и проекта службы доступны в репозитории для пересборки. При изменении или распространении сторонних файлов сохраняйте их собственные условия, уведомления и требуемые исходники. Установленная служба получает копии документов в своём каталоге `docs/`; не удаляйте их из отдельного дистрибутива службы.

Внешний подписанный драйвер [PawnIO](https://pawnio.eu/) устанавливается пользователем отдельно. Его бинарные файлы, установщик и исходники не включены в ZIP Luma. Это отличается от встроенных в LHM модулей PawnIO.Modules, описанных выше.

## Необязательный NVIDIA FrameView SDK

Интеграция FPS обращается к отдельно установленному NVIDIA FrameView SDK. DLL, исполняемые файлы помощников, служба, заголовки, статические библиотеки и исходники SDK не входят в дистрибутив Luma. Его условия не заменяются лицензией MIT Luma, а лицензия SDK не включается в этот комплект. Сведения о продукте: [NVIDIA FrameView](https://www.nvidia.com/en-us/geforce/technologies/frameview/).

## Комплект переносимого релиза

Для температуры GPU Luma динамически обращается к NVML из уже установленного драйвера NVIDIA. NVML, NVAPI, драйверы, заголовки и SDK NVIDIA в исходники и архив Luma не включаются. Эта интеграция не меняет лицензию установленного драйвера; [документация NVML](https://docs.nvidia.com/deploy/nvml-api/latest/index.html).

Вместе с бинарными файлами сохраняйте корневую лицензию Luma, этот документ и весь каталог `docs/licenses/`, сохранив их относительное расположение.

Patch-версия runtime в проекте не закреплена: повторная сборка с другой версией .NET SDK 10.x, в том числе в CI, может разрешить другой runtime-пакет даже при той же версии Luma. Перед упаковкой такой сборки нужно сверить фактические пакеты из `project.assets.json`, версии выходных DLL, лицензии и уведомления; затем обновить этот перечень и копии соответствующих файлов. Упаковщик останавливает сборку архива при несовпадении перечня runtime-пакетов.

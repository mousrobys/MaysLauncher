# MaysLauncher — правила проекта

## Сборка APK
- Проект: `C:\Users\moysecamm\Documents\лаунчер\android\MaysLauncher`
- Команда: `C:\Gradle\gradle-8.13\bin\gradle.bat assembleDebug --no-daemon`
- `JAVA_HOME="C:\Program Files\Eclipse Adoptium\jdk-17.0.20.8-hotspot"`, `ANDROID_HOME=C:\Android`

## ВАЖНО: выкладка билда
- После каждой успешной сборки `assembleDebug` ОБЯЗАТЕЛЬНО заменить старый APK в папке:
  `C:\Users\moysecamm\Documents\лаунчер\билды exe\билды апк\MaysLauncher.apk`
- Действие: удалить старые `.apk` в этой папке и скопировать туда
  `app\build\outputs\apk\debug\app-debug.apk` под именем `MaysLauncher.apk`.
- Файл всегда называется `MaysLauncher.apk` (без суффиксов версий).
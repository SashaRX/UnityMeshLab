# Version Gates

```csharp
#if UNITY_2022_2_OR_NEWER
    // Новый API
#else
    // Фолбэк
#endif
```

## versionDefines в asmdef
```json
"versionDefines": [{
  "name": "com.unity.formats.fbx",
  "expression": "[5.0.0,6.0.0)",
  "define": "HAS_FBX_EXPORTER"
}]
```

| Директива | Версия |
|-----------|--------|
| UNITY_2021_3_OR_NEWER | 2021.3 LTS |
| UNITY_2022_2_OR_NEWER | 2022.2 |
| UNITY_6000_0_OR_NEWER | Unity 6 |

Всегда предоставляй фолбэк в #else.

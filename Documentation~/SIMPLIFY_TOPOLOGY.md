# Remesh / Simplify: отверстия после Trim и схлопнутые лепестки

Дополнение: [подгонка и перестройка voxel triangles по source](VOXEL_SURFACE_REFINE.md)
устраняет часть grid diagonal/sliver artifacts, сохраняя описанные ниже topology gates.

2026-10-04, продолжение диагностики входной геометрии Experiment #1.

## Причина на бюсте

Проверен source `Meshy_AI_Distinguished_Bust_0925154648_texture` в изолированном
Unity 6000.2.6f2. Параметры повторяют скриншот: voxel 128, fit/trim включены,
min part 0.043, min rod 0.8, Simplify target 1600, maximum error 0.0041,
Regularize None, Preserve folds / Remove small parts включены.

| Стадия до исправления | Faces | Boundary edges | Non-manifold edges | Duplicate face pairs | Euler V−E+F |
|---|---:|---:|---:|---:|---:|
| Source, position-welded | 30288 | 0 | 0 | 0 | 2 |
| Raw voxel remesh | 51616 | 0 | 0 | 0 | 2 |
| Trim output | 51612 | 12 | 0 | 0 | −2 |
| Simplify output | 1901 | 3 | 3 | 3 | 4 |

Trim удалял три лица как back of a sheet и одно как rim/no source на **замкнутом
source**. Ориентация маленькой fitted voxel face в резком углу не обязательно
совпадает с нормалью ближайшей исходной грани. Такой тест подходит для удаления
обратной стороны открытого листа, но здесь открывал четыре отверстия.

Native `simplifyWithUpdate` отдельно создавал три пары совпадающих треугольников
с противоположным winding. Каждый такой плоский лепесток был прикреплён к сетке
одним ребром с четырьмя соседними faces. Это не обычная ошибка отображения UV.
Сохранённый предыдущий replay на 1997 faces тоже имеет 3 boundary edges,
2 non-manifold edges и 2 duplicate pairs; он не выдаётся за точный snapshot
скриншота на 1883 faces.

## Исправление

`RemeshTopology` проверяет связность по точным геометрическим позициям, независимо
от splits normals/UV: repeated triangles, нулевую площадь в double precision,
число/направление соседей ребра и разорванные vertex fans. Для connected
components измеряется Euler characteristic.

1. Trim выделяет ориентированные замкнутые source components с ненулевым объёмом.
   Их поверхность сохраняется без sheet-facing reject. В смешанном source
   ближайшая source face определяет принадлежность к закрытому component;
   поиск ограничен прежним maxDistance. Открытые sheets сохраняют прежнюю
   facing-классификацию. Две противоположные копии плоского треугольника не
   считаются замкнутым объёмом.
2. Simplify проверяет вход и фиксирует существующие boundary vertices через
   native LockBorder. Независимые флаги и сохранённые настройки не переименованы.
3. После native Simplify удаляются только opposite-winding duplicate pairs,
   прикреплённые к основной поверхности. На каждом из трёх рёбер после удаления
   должно остаться ноль соседей или два с согласованным winding. Удаляется пара
   целиком, без изменения оставшихся positions/triangles, затем unused vertices
   compacted. Изолированные двойные sheets и пары, удаление которых откроет
   соседнюю грань, не принимаются как безопасный repair.
4. Postflight требует валидную локальную topology, **точно тот же набор boundary
   segments** и сохранение Euler characteristics компонентов. Prune разрешает
   сокращение multiset component characteristics; topology check не разрешает
   исчезновение настоящих открытых borders. Такие prune candidates отклоняются.
5. Неприемлемый/пустой результат повторяется с меньшей ошибкой и большим triangle
   budget, всегда от исходного input. После шести неудач сохраняется копия input
   с error=0 и предупреждением. Некорректный вход отклоняется с сообщением;
   одиночное удаление плохой грани для скрытия ошибки не выполняется.

Это managed postflight на том же native Simplify ABI. `Native~/` и `Plugins/`
не менялись. Source asset, serialized settings и UV-merge алгоритм не изменены.

## Измеренный результат

| Стадия после исправления | Vertices | Faces | Boundary | Non-manifold | Duplicates | Zero / near-zero area | Euler |
|---|---:|---:|---:|---:|---:|---:|---:|
| Trim | 25810 | 51616 | 0 | 0 | 0 | 0 / 0 | 2 |
| Simplify None | 945 | 1886 | 0 | 0 | 0 | 0 / 0 | 2 |
| Simplify Light | 1592 | 3180 | 0 | 0 | 0 | 0 / 0 | 2 |
| Simplify Strong | 5923 | 11842 | 0 | 0 | 0 | 0 / 0 | 2 |

Near-zero threshold для независимого Python check соответствует native Clean:
area ≤ maxExtent² × float32 epsilon. У None/Light удалены 6 fin faces;
у Strong таких пар нет. Все три режима сохраняют один замкнутый component.
Девять вызовов Simplify (три на режим) дали бит-идентичные position/index buffers.
None достигает error 0.00402533 при заданном limit 0.0041; эта ошибка относится
к native simplification, не к trim и не к полной ошибке source→voxel→simplify.

На исправленном None output Unwrap даёт 72 islands без merge и 46 с merge,
в обоих случаях полный UV overlap scan чистый, каждый source corner сохранён.
Это проверка работоспособности downstream, не паритет с ручным artist layout.

## Проверка и воспроизведение

192/192 связанных Unity EditMode tests passed, skipped 0. Включены:

- sharp-corner trim в двух масштабах и смешанный source volume + open sheet;
- безопасные fins, splits по совпадающим positions и unsafe duplicate pairs;
- disconnected vertex fans и закрытый torus против sphere topology;
- aggressive/empty simplification, native Clean на очень тонком замкнутом input;
- настоящий open grid с неизменными borders, cancellation;
- существующие bake, normals, hierarchy, merge/relax и UV diagnostics tests.

C# compile check проходит с FBX exporter define и без него.

Developer harness: [SimplifyTopologyReplay.cs](../Tools~/SimplifyTopologyReplay.cs).
Скопировать в `Assets/Editor` изолированного Unity-проекта, подключённого к этой
версии package, импортировать readable source как `Assets/Bust.fbx`, запустить
`-batchmode -executeMethod SimplifyTopologyReplay.Run`. Harness создаёт временную
копию FBX, получает snapshots source/raw/trim/simplify, проверяет три режима
и Unwrap; EditorPrefs и source asset не меняет. Geometry buffers остаются
локально в `PROJECT/simplify-topology/`, не публикуются в Git.

Независимая проверка требует NumPy:

```text
python Tools~/analyze_simplify_topology.py PROJECT/simplify-topology/raw-0.bin PROJECT/simplify-topology/trim-0.bin PROJECT/simplify-topology/simplify-0.bin
```

В существующей Unity-сессии нужно заново выполнить **Remesh → Simplify → Unwrap**:
старый trimmed snapshot уже содержит отверстия, и его реальные borders нельзя
отличить от намеренных отверстий лишь по сетке.

Проверки topology/UV не являются полным поиском геометрических self-intersections
в 3D и не устанавливают пределы формы по high-poly source. Euler characteristics
не идентифицируют spatial correspondence компонентов. Исправление устраняет
воспроизведённые отверстия и fins, сохраняет реальные borders и не обещает
исправлять произвольную повреждённую исходную сетку или все skinny triangles.

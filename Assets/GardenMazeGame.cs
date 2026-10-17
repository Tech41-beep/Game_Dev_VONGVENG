using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// GARDEN MAZE - a complete small game in one script.
// Drop this file anywhere in Assets (NOT in an Editor folder), open an empty scene and press Play.
// It builds everything at runtime: title screen, 5 random hedge mazes that get bigger,
// a walking character, countdown timer, route hints, score, best times and win/lose screens.
public class GardenMazeGame : MonoBehaviour
{
    // Set to false if you prefer to add this component to an empty GameObject yourself
    // (needed if you want to assign SimpleNaturePack prefabs in the Inspector below).
    static readonly bool AutoStart = true;

    [Tooltip("Optional: trees, bushes, rocks... scattered around the outside of the maze.")]
    public GameObject[] decorationPrefabs;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!AutoStart) return;
        if (FindFirstObjectByType<GardenMazeGame>() != null) return;
        new GameObject("GardenMazeGame").AddComponent<GardenMazeGame>();
    }

    // ------------------------------------------------------------------ settings
    enum State { Menu, Playing, Paused, LevelComplete, TimeUp, Won }

    static readonly int[] LevelSizes = { 6, 8, 10, 13, 16 }; // cells per side
    const float CellSize = 3f;
    const float WallHeight = 3.2f;
    const float WalkSpeed = 5f;
    const float SprintSpeed = 8f;
    const float Gravity = -20f;
    const float LookSensitivity = 0.12f;
    const int HintsPerLevel = 3;
    const float HintSeconds = 6f;
    const float Margin = 20f;
    const int DecorCount = 70;

    class Theme
    {
        public Color hedge, grass, leaf, fog;
        public float sun;
        public Theme(Color hedge, Color grass, Color leaf, Color fog, float sun)
        { this.hedge = hedge; this.grass = grass; this.leaf = leaf; this.fog = fog; this.sun = sun; }
    }

    static readonly Theme[] Themes =
    {
        new Theme(new Color(.13f,.45f,.16f), new Color(.35f,.60f,.25f), new Color(.15f,.55f,.20f), new Color(.75f,.90f,1.0f), 1.1f), // spring
        new Theme(new Color(.08f,.50f,.22f), new Color(.45f,.70f,.20f), new Color(.10f,.60f,.25f), new Color(.80f,.92f,1.0f), 1.3f), // summer
        new Theme(new Color(.55f,.30f,.08f), new Color(.55f,.50f,.20f), new Color(.80f,.40f,.10f), new Color(1.0f,.85f,.70f), 1.0f), // autumn
        new Theme(new Color(.10f,.35f,.30f), new Color(.85f,.90f,.95f), new Color(.15f,.40f,.35f), new Color(.82f,.88f,.96f), 0.9f), // winter
        new Theme(new Color(.07f,.22f,.12f), new Color(.15f,.28f,.15f), new Color(.08f,.28f,.14f), new Color(.35f,.25f,.45f), 0.5f), // twilight
    };

    // ------------------------------------------------------------------ state
    State state = State.Menu;
    int level;
    int score;
    int highScore;
    int hintsLeft;
    int hintsUsed;
    int lastLevelScore;
    bool lastWasBest;
    float timeLeft;
    float levelElapsed;
    float hintTimer;
    float pitch;
    float verticalVelocity;
    float walkPhase;
    float menuAngle;
    bool thirdPerson;

    // maze
    bool[,] g;
    int W, H;

    // scene objects
    Shader baseShader;
    Camera cam;
    Light sun;
    Transform levelRoot;
    Transform hintRoot;
    readonly List<Material> levelMats = new List<Material>();

    // player
    GameObject playerObj;
    CharacterController cc;
    Transform camPivot;
    Transform legL, legR, armL, armR;
    readonly List<Renderer> bodyRenderers = new List<Renderer>();

    // UI
    GUIStyle labelStyle, buttonStyle;

    // ------------------------------------------------------------------ input (new Input System or old Input Manager)
    static Vector2 MoveInput()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k == null) return Vector2.zero;
        float x = ((k.dKey.isPressed || k.rightArrowKey.isPressed) ? 1f : 0f) - ((k.aKey.isPressed || k.leftArrowKey.isPressed) ? 1f : 0f);
        float y = ((k.wKey.isPressed || k.upArrowKey.isPressed) ? 1f : 0f) - ((k.sKey.isPressed || k.downArrowKey.isPressed) ? 1f : 0f);
        return new Vector2(x, y);
#else
        return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
    }

    static Vector2 MouseDelta()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
#else
        return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
#endif
    }

    static bool SprintHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
#else
        return Input.GetKey(KeyCode.LeftShift);
#endif
    }

    static bool EscapePressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    static bool ConfirmPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        return k != null && (k.spaceKey.wasPressedThisFrame || k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return);
#endif
    }

    static bool HintPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.H);
#endif
    }

    static bool ViewPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.V);
#endif
    }

    // ------------------------------------------------------------------ setup
    void Awake()
    {
        var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseShader = probe.GetComponent<Renderer>().sharedMaterial.shader; // the project's default lit shader (URP Lit)
        Destroy(probe);

        highScore = PlayerPrefs.GetInt("GardenMaze_High", 0);

        SetupCamera();
        SetupLight();
        CreatePlayer();
        ShowMenu();
    }

    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 400f;
        cam.fieldOfView = 70f;
    }

    void SetupLight()
    {
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sun = l; break; }
        if (sun == null)
        {
            var go = new GameObject("Sun");
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
        sun.shadows = LightShadows.Soft;
    }

    Material MakeMat(Color c, float glow = 0f)
    {
        var m = new Material(baseShader);
        m.color = c;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.1f);
        if (glow > 0f)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * glow);
        }
        return m;
    }

    Material LevelMat(Color c, float glow = 0f)
    {
        var m = MakeMat(c, glow);
        levelMats.Add(m);
        return m;
    }

    GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 localScale, Material mat, bool keepCollider = false)
    {
        var go = GameObject.CreatePrimitive(type);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        if (!keepCollider) DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    Transform Limb(string name, Transform parent, Vector3 pivotPos, Vector3 partPos, Vector3 partScale, Material mat)
    {
        var pivot = new GameObject(name).transform;
        pivot.SetParent(parent, false);
        pivot.localPosition = pivotPos;
        Prim(PrimitiveType.Capsule, pivot, partPos, partScale, mat);
        return pivot;
    }

    void CreatePlayer()
    {
        playerObj = new GameObject("Player");
        playerObj.transform.SetParent(transform, false);

        cc = playerObj.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0f, 0.9f, 0f);

        var shirt = MakeMat(new Color(0.9f, 0.3f, 0.2f));
        var pants = MakeMat(new Color(0.2f, 0.25f, 0.5f));
        var skin = MakeMat(new Color(0.85f, 0.65f, 0.5f));

        var t = playerObj.transform;
        Prim(PrimitiveType.Capsule, t, new Vector3(0f, 1.05f, 0f), new Vector3(0.5f, 0.45f, 0.3f), shirt);
        Prim(PrimitiveType.Sphere, t, new Vector3(0f, 1.72f, 0f), Vector3.one * 0.3f, skin);
        legL = Limb("LegL", t, new Vector3(-0.13f, 0.8f, 0f), new Vector3(0f, -0.4f, 0f), new Vector3(0.18f, 0.4f, 0.18f), pants);
        legR = Limb("LegR", t, new Vector3(0.13f, 0.8f, 0f), new Vector3(0f, -0.4f, 0f), new Vector3(0.18f, 0.4f, 0.18f), pants);
        armL = Limb("ArmL", t, new Vector3(-0.32f, 1.4f, 0f), new Vector3(0f, -0.3f, 0f), new Vector3(0.14f, 0.3f, 0.14f), shirt);
        armR = Limb("ArmR", t, new Vector3(0.32f, 1.4f, 0f), new Vector3(0f, -0.3f, 0f), new Vector3(0.14f, 0.3f, 0.14f), shirt);

        camPivot = new GameObject("CameraPivot").transform;
        camPivot.SetParent(t, false);
        camPivot.localPosition = new Vector3(0f, 1.65f, 0f);

        bodyRenderers.AddRange(playerObj.GetComponentsInChildren<Renderer>());
        SetBodyVisible(false);
        playerObj.SetActive(false);
    }

    void SetBodyVisible(bool visible)
    {
        foreach (var r in bodyRenderers)
            r.shadowCastingMode = visible ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
    }

    // ------------------------------------------------------------------ level building
    void ClearLevel()
    {
        if (levelRoot != null) Destroy(levelRoot.gameObject);
        foreach (var m in levelMats) if (m != null) Destroy(m);
        levelMats.Clear();
        hintRoot = null;
    }

    void BuildMaze(int lvl)
    {
        ClearLevel();

        var theme = Themes[lvl % Themes.Length];
        int cells = LevelSizes[lvl];
        W = cells * 2 + 1;
        H = cells * 2 + 1;
        var rng = new System.Random();
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        levelRoot = new GameObject("Level").transform;
        levelRoot.SetParent(transform, false);

        var hedgeMat = LevelMat(theme.hedge);
        var grassMat = LevelMat(theme.grass);
        var trunkMat = LevelMat(new Color(0.4f, 0.27f, 0.15f));
        var leafMat = LevelMat(theme.leaf);
        var goldMat = LevelMat(new Color(1f, 0.8f, 0.1f), 2.5f);

        // --- layout: recursive backtracker. true = hedge
        g = new bool[W, H];
        for (int x = 0; x < W; x++)
            for (int z = 0; z < H; z++)
                g[x, z] = true;

        var dirs = new[] { new Vector2Int(2, 0), new Vector2Int(-2, 0), new Vector2Int(0, 2), new Vector2Int(0, -2) };
        var stack = new Stack<Vector2Int>();
        g[1, 1] = false;
        stack.Push(new Vector2Int(1, 1));
        var options = new List<Vector2Int>();
        while (stack.Count > 0)
        {
            var c = stack.Peek();
            options.Clear();
            foreach (var d in dirs)
            {
                var p = c + d;
                if (p.x >= 1 && p.x <= W - 2 && p.y >= 1 && p.y <= H - 2 && g[p.x, p.y]) options.Add(d);
            }
            if (options.Count == 0) { stack.Pop(); continue; }
            var pick = options[rng.Next(options.Count)];
            g[c.x + pick.x / 2, c.y + pick.y / 2] = false;
            g[c.x + pick.x, c.y + pick.y] = false;
            stack.Push(c + pick);
        }
        g[0, 1] = false;         // entrance (west)
        g[W - 1, H - 2] = false; // exit (east)

        // --- hedges (horizontal runs merged into one block)
        var hedges = new GameObject("Hedges").transform;
        hedges.SetParent(levelRoot, false);
        for (int z = 0; z < H; z++)
        {
            int x = 0;
            while (x < W)
            {
                if (!g[x, z]) { x++; continue; }
                int start = x;
                while (x < W && g[x, z]) x++;
                int len = x - start;
                Prim(PrimitiveType.Cube, hedges,
                    new Vector3((start + len / 2f) * CellSize, WallHeight / 2f, (z + 0.5f) * CellSize),
                    new Vector3(len * CellSize, WallHeight, CellSize), hedgeMat, true);
            }
        }

        // --- ground + invisible fence around the garden
        float mazeW = W * CellSize, mazeH = H * CellSize;
        Prim(PrimitiveType.Cube, levelRoot, new Vector3(mazeW / 2f, -0.5f, mazeH / 2f),
            new Vector3(mazeW + Margin * 2f, 1f, mazeH + Margin * 2f), grassMat, true);

        float minX = -Margin, maxX = mazeW + Margin, minZ = -Margin, maxZ = mazeH + Margin;
        float midX = (minX + maxX) / 2f, midZ = (minZ + maxZ) / 2f;
        var fenceMat = grassMat;
        Fence(new Vector3(minX - 0.5f, 5f, midZ), new Vector3(1f, 10f, maxZ - minZ + 2f), fenceMat);
        Fence(new Vector3(maxX + 0.5f, 5f, midZ), new Vector3(1f, 10f, maxZ - minZ + 2f), fenceMat);
        Fence(new Vector3(midX, 5f, minZ - 0.5f), new Vector3(maxX - minX + 2f, 10f, 1f), fenceMat);
        Fence(new Vector3(midX, 5f, maxZ + 0.5f), new Vector3(maxX - minX + 2f, 10f, 1f), fenceMat);

        // --- decorations
        var decor = new GameObject("Decorations").transform;
        decor.SetParent(levelRoot, false);
        var prefabs = new List<GameObject>();
        if (decorationPrefabs != null)
            foreach (var p in decorationPrefabs) if (p != null) prefabs.Add(p);
        var keepOut = new Rect(-3f, -3f, mazeW + 6f, mazeH + 6f);
        for (int i = 0; i < DecorCount; i++)
        {
            for (int tries = 0; tries < 30; tries++)
            {
                float px = R(minX + 1f, maxX - 1f);
                float pz = R(minZ + 1f, maxZ - 1f);
                if (keepOut.Contains(new Vector2(px, pz))) continue;

                GameObject obj;
                if (prefabs.Count > 0)
                {
                    obj = Instantiate(prefabs[rng.Next(prefabs.Count)], decor);
                    obj.transform.localScale *= R(0.8f, 1.3f);
                }
                else
                {
                    obj = MakeTree(R(3f, 6f), trunkMat, leafMat);
                    obj.transform.SetParent(decor, false);
                }
                obj.transform.localPosition = new Vector3(px, 0f, pz);
                obj.transform.localRotation = Quaternion.Euler(0f, R(0f, 360f), 0f);
                break;
            }
        }

        // --- goal: glowing orb, tall light beam (visible over the hedges) and a point light
        var goalPos = new Vector3((W - 2 + 0.5f) * CellSize, 0f, (H - 2 + 0.5f) * CellSize);
        Prim(PrimitiveType.Sphere, levelRoot, goalPos + Vector3.up * 1.2f, Vector3.one * 0.8f, goldMat);
        Prim(PrimitiveType.Cylinder, levelRoot, goalPos + Vector3.up * 9f, new Vector3(0.3f, 9f, 0.3f), goldMat);
        var glow = new GameObject("GoalLight").AddComponent<Light>();
        glow.transform.SetParent(levelRoot, false);
        glow.transform.localPosition = goalPos + Vector3.up * 1.5f;
        glow.type = LightType.Point;
        glow.color = new Color(1f, 0.85f, 0.3f);
        glow.range = CellSize * 4f;
        glow.intensity = 3f;

        // --- atmosphere
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = theme.fog;
        RenderSettings.fogDensity = 0.012f;
        if (sun != null) sun.intensity = theme.sun;
    }

    void Fence(Vector3 pos, Vector3 scale, Material mat)
    {
        var go = Prim(PrimitiveType.Cube, levelRoot, pos, scale, mat, true);
        go.GetComponent<Renderer>().enabled = false;
    }

    GameObject MakeTree(float trunkHeight, Material trunkMat, Material leafMat)
    {
        var tree = new GameObject("Tree");
        Prim(PrimitiveType.Cylinder, tree.transform, new Vector3(0f, trunkHeight / 2f, 0f),
            new Vector3(0.5f, trunkHeight / 2f, 0.5f), trunkMat, true);
        float crown = trunkHeight * 0.9f;
        Prim(PrimitiveType.Sphere, tree.transform, new Vector3(0f, trunkHeight + crown * 0.25f, 0f),
            Vector3.one * crown, leafMat);
        return tree;
    }

    // ------------------------------------------------------------------ game flow
    void ShowMenu()
    {
        level = 0;
        score = 0;
        BuildMaze(0);
        playerObj.SetActive(false);
        cam.transform.SetParent(null);
        state = State.Menu;
        UnlockCursor();
    }

    void StartGame()
    {
        score = 0;
        BeginLevel(0);
    }

    void BeginLevel(int lvl)
    {
        level = lvl;
        BuildMaze(lvl);

        playerObj.transform.position = new Vector3(0.5f * CellSize, 0.1f, 1.5f * CellSize);
        playerObj.transform.rotation = Quaternion.Euler(0f, 90f, 0f); // facing into the maze
        playerObj.SetActive(true);
        cc.enabled = false;
        cc.enabled = true;

        cam.transform.SetParent(camPivot, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        pitch = 0f;
        camPivot.localRotation = Quaternion.identity;
        verticalVelocity = 0f;
        thirdPerson = false;
        SetBodyVisible(false);

        hintsLeft = HintsPerLevel;
        hintsUsed = 0;
        levelElapsed = 0f;
        timeLeft = 60f + LevelSizes[lvl] * LevelSizes[lvl];

        state = State.Playing;
        LockCursor();
    }

    void LevelDone()
    {
        int bonus = Mathf.RoundToInt(timeLeft * 10f);
        lastLevelScore = Mathf.Max(100, 500 + bonus - hintsUsed * 100);
        score += lastLevelScore;

        string key = "GardenMaze_Best" + level;
        float best = PlayerPrefs.GetFloat(key, 0f);
        lastWasBest = best <= 0f || levelElapsed < best;
        if (lastWasBest) PlayerPrefs.SetFloat(key, levelElapsed);
        if (score > highScore)
        {
            highScore = score;
            PlayerPrefs.SetInt("GardenMaze_High", highScore);
        }
        PlayerPrefs.Save();

        state = level >= LevelSizes.Length - 1 ? State.Won : State.LevelComplete;
        UnlockCursor();
    }

    void LockCursor() { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    void UnlockCursor() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

    void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ------------------------------------------------------------------ update
    void Update()
    {
        switch (state)
        {
            case State.Menu:
                UpdateMenuCamera();
                if (ConfirmPressed()) StartGame();
                break;
            case State.Playing:
                UpdatePlaying();
                break;
            case State.Paused:
                if (EscapePressed()) { state = State.Playing; LockCursor(); }
                break;
            case State.LevelComplete:
                if (ConfirmPressed()) BeginLevel(level + 1);
                break;
            case State.TimeUp:
                if (ConfirmPressed()) BeginLevel(level);
                break;
            case State.Won:
                if (ConfirmPressed()) ShowMenu();
                break;
        }
    }

    void UpdateMenuCamera()
    {
        menuAngle += 12f * Time.deltaTime;
        var center = new Vector3(W * CellSize / 2f, 0f, H * CellSize / 2f);
        float radius = W * CellSize * 0.6f;
        float a = menuAngle * Mathf.Deg2Rad;
        cam.transform.position = center + new Vector3(Mathf.Sin(a) * radius, W * CellSize * 0.75f, Mathf.Cos(a) * radius);
        cam.transform.LookAt(center);
    }

    void UpdatePlaying()
    {
        float dt = Time.deltaTime;

        if (EscapePressed()) { state = State.Paused; UnlockCursor(); return; }
        if (HintPressed()) UseHint();
        if (ViewPressed())
        {
            thirdPerson = !thirdPerson;
            SetBodyVisible(thirdPerson);
        }

        levelElapsed += dt;
        timeLeft -= dt;
        if (timeLeft <= 0f)
        {
            timeLeft = 0f;
            state = State.TimeUp;
            UnlockCursor();
            return;
        }

        // look
        Vector2 look = MouseDelta() * LookSensitivity;
        playerObj.transform.Rotate(0f, look.x, 0f);
        pitch = Mathf.Clamp(pitch - look.y, -70f, 70f);
        camPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        // move
        Vector2 m = MoveInput();
        Vector3 dir = playerObj.transform.right * m.x + playerObj.transform.forward * m.y;
        if (dir.sqrMagnitude > 1f) dir.Normalize();
        float speed = SprintHeld() ? SprintSpeed : WalkSpeed;

        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += Gravity * dt;
        cc.Move((dir * speed + Vector3.up * verticalVelocity) * dt);

        // simple walking animation
        float moving = Mathf.Clamp01(dir.magnitude);
        if (moving > 0.01f) walkPhase += speed * dt * 1.8f;
        float swing = Mathf.Sin(walkPhase) * 40f * moving;
        legL.localRotation = Quaternion.Euler(swing, 0f, 0f);
        legR.localRotation = Quaternion.Euler(-swing, 0f, 0f);
        armL.localRotation = Quaternion.Euler(-swing, 0f, 0f);
        armR.localRotation = Quaternion.Euler(swing, 0f, 0f);

        // hint expiry
        if (hintRoot != null)
        {
            hintTimer -= dt;
            if (hintTimer <= 0f) { Destroy(hintRoot.gameObject); hintRoot = null; }
        }

        // reached the exit?
        if (playerObj.transform.position.x > (W - 1 + 0.3f) * CellSize) LevelDone();
    }

    void LateUpdate()
    {
        if (cam == null || camPivot == null || cam.transform.parent != camPivot) return;

        if (!thirdPerson)
        {
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;
            return;
        }

        // third person: camera behind and above the head, pulled in if a hedge is in the way
        Vector3 dir = camPivot.TransformDirection(new Vector3(0f, 0.35f, -1f).normalized);
        float dist = 2.6f;
        if (Physics.Raycast(camPivot.position, dir, out RaycastHit hit, dist + 0.3f, ~0, QueryTriggerInteraction.Ignore))
            dist = Mathf.Max(0.2f, hit.distance - 0.3f);
        cam.transform.position = camPivot.position + dir * dist;
        cam.transform.rotation = camPivot.rotation;
    }

    // ------------------------------------------------------------------ hints (shortest route, found with BFS)
    void UseHint()
    {
        if (hintsLeft <= 0 || hintRoot != null) return;
        var route = FindRoute(playerObj.transform.position);
        if (route.Count == 0) return;

        hintsLeft--;
        hintsUsed++;
        hintTimer = HintSeconds;

        hintRoot = new GameObject("Hint").transform;
        hintRoot.SetParent(levelRoot, false);
        var mat = LevelMat(new Color(0.3f, 0.9f, 1f), 3f);
        foreach (var p in route)
        {
            var dot = Prim(PrimitiveType.Cube, hintRoot, p, new Vector3(0.6f, 0.06f, 0.6f), mat);
            dot.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
        }
    }

    List<Vector3> FindRoute(Vector3 from)
    {
        int sx = Mathf.Clamp(Mathf.FloorToInt(from.x / CellSize), 0, W - 1);
        int sz = Mathf.Clamp(Mathf.FloorToInt(from.z / CellSize), 0, H - 1);
        int goalIdx = (H - 2) * W + (W - 1);

        var prev = new int[W * H];
        for (int i = 0; i < prev.Length; i++) prev[i] = -2;
        var queue = new Queue<int>();
        int startIdx = sz * W + sx;
        prev[startIdx] = -1;
        queue.Enqueue(startIdx);

        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (c == goalIdx) break;
            int cxx = c % W, czz = c / W;
            for (int d = 0; d < 4; d++)
            {
                int nx = cxx + dx[d], nz = czz + dz[d];
                if (nx < 0 || nz < 0 || nx >= W || nz >= H || g[nx, nz]) continue;
                int idx = nz * W + nx;
                if (prev[idx] != -2) continue;
                prev[idx] = c;
                queue.Enqueue(idx);
            }
        }

        var route = new List<Vector3>();
        if (prev[goalIdx] == -2) return route;
        for (int c = goalIdx; c != -1; c = prev[c])
            route.Add(new Vector3((c % W + 0.5f) * CellSize, 0.05f, (c / W + 0.5f) * CellSize));
        return route;
    }

    // ------------------------------------------------------------------ UI (IMGUI - no canvas setup needed)
    void EnsureStyles()
    {
        if (labelStyle != null) return;
        labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 26, fontStyle = FontStyle.Bold };
    }

    static void DrawPanel(Rect r, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    void DrawLabel(Rect r, string text, int size, Color col, TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        labelStyle.fontSize = size;
        labelStyle.alignment = anchor;
        labelStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, labelStyle);
        labelStyle.normal.textColor = col;
        GUI.Label(r, text, labelStyle);
    }

    bool DrawButton(Rect r, string text) => GUI.Button(r, text, buttonStyle);

    void OnGUI()
    {
        EnsureStyles();
        float s = Screen.height / 720f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float vw = Screen.width / s;
        float cx = vw / 2f;
        var gold = new Color(1f, 0.85f, 0.3f);
        var white = Color.white;

        switch (state)
        {
            case State.Menu:
                DrawPanel(new Rect(0, 0, vw, 720), new Color(0, 0, 0, 0.35f));
                DrawLabel(new Rect(0, 60, vw, 110), "GARDEN MAZE", 84, new Color(0.7f, 1f, 0.6f));
                DrawLabel(new Rect(0, 170, vw, 40), "Find the way out before time runs out", 28, white);
                if (DrawButton(new Rect(cx - 150, 260, 300, 60), "PLAY")) StartGame();
                if (DrawButton(new Rect(cx - 150, 340, 300, 60), "QUIT")) QuitGame();
                DrawLabel(new Rect(0, 420, vw, 40), "High score: " + highScore, 26, gold);
                DrawLabel(new Rect(cx - 450, 520, 900, 100),
                    "WASD / Arrows - walk     Mouse - look     Shift - run\nH - show the route (3 per level)     V - change camera     Esc - pause", 24, white);
                break;

            case State.Playing:
                DrawHud(vw, cx, gold, white);
                break;

            case State.Paused:
                DrawHud(vw, cx, gold, white);
                DrawPanel(new Rect(0, 0, vw, 720), new Color(0, 0, 0, 0.55f));
                DrawLabel(new Rect(0, 120, vw, 80), "PAUSED", 64, white);
                if (DrawButton(new Rect(cx - 160, 240, 320, 60), "RESUME")) { state = State.Playing; LockCursor(); }
                if (DrawButton(new Rect(cx - 160, 320, 320, 60), "RESTART LEVEL")) BeginLevel(level);
                if (DrawButton(new Rect(cx - 160, 400, 320, 60), "MAIN MENU")) ShowMenu();
                break;

            case State.LevelComplete:
                DrawPanel(new Rect(0, 0, vw, 720), new Color(0, 0, 0, 0.55f));
                DrawLabel(new Rect(0, 100, vw, 80), "LEVEL COMPLETE!", 64, new Color(0.7f, 1f, 0.6f));
                DrawLabel(new Rect(0, 200, vw, 40), "Time: " + levelElapsed.ToString("F1") + "s" + (lastWasBest ? "   NEW BEST!" : ""), 30, white);
                DrawLabel(new Rect(0, 245, vw, 40), "+" + lastLevelScore + " points   (hints used: " + hintsUsed + ")", 28, gold);
                DrawLabel(new Rect(0, 290, vw, 40), "Score: " + score, 32, gold);
                if (DrawButton(new Rect(cx - 160, 370, 320, 60), "NEXT LEVEL  (Space)")) BeginLevel(level + 1);
                break;

            case State.TimeUp:
                DrawPanel(new Rect(0, 0, vw, 720), new Color(0, 0, 0, 0.6f));
                DrawLabel(new Rect(0, 120, vw, 80), "TIME'S UP!", 72, new Color(1f, 0.4f, 0.3f));
                DrawLabel(new Rect(0, 215, vw, 40), "The garden swallowed you. Try again - the maze will be new.", 26, white);
                if (DrawButton(new Rect(cx - 160, 300, 320, 60), "TRY AGAIN  (Space)")) BeginLevel(level);
                if (DrawButton(new Rect(cx - 160, 380, 320, 60), "MAIN MENU")) ShowMenu();
                break;

            case State.Won:
                DrawPanel(new Rect(0, 0, vw, 720), new Color(0, 0, 0, 0.6f));
                DrawLabel(new Rect(0, 90, vw, 80), "YOU ESCAPED EVERY GARDEN!", 56, new Color(0.7f, 1f, 0.6f));
                DrawLabel(new Rect(0, 200, vw, 40), "Final score: " + score, 40, gold);
                DrawLabel(new Rect(0, 250, vw, 40), "High score: " + highScore, 28, white);
                if (DrawButton(new Rect(cx - 160, 340, 320, 60), "PLAY AGAIN  (Space)")) ShowMenu();
                break;
        }
    }

    void DrawHud(float vw, float cx, Color gold, Color white)
    {
        DrawPanel(new Rect(16, 16, 260, 84), new Color(0, 0, 0, 0.4f));
        DrawLabel(new Rect(16, 16, 260, 40), "Level " + (level + 1) + " / " + LevelSizes.Length, 28, white);
        DrawLabel(new Rect(16, 54, 260, 40), "Score " + score, 24, gold);

        int t = Mathf.CeilToInt(Mathf.Max(0f, timeLeft));
        string ts = (t / 60) + ":" + (t % 60).ToString("00");
        var tc = timeLeft < 20f ? new Color(1f, 0.35f, 0.3f) : white;
        DrawPanel(new Rect(cx - 80, 16, 160, 60), new Color(0, 0, 0, 0.4f));
        DrawLabel(new Rect(cx - 80, 16, 160, 60), ts, 44, tc);

        DrawPanel(new Rect(vw - 276, 16, 260, 52), new Color(0, 0, 0, 0.4f));
        DrawLabel(new Rect(vw - 276, 16, 260, 52), "Hints: " + hintsLeft + "  (H)", 26, hintsLeft > 0 ? white : new Color(1, 1, 1, 0.5f));

        if (!thirdPerson) DrawPanel(new Rect(cx - 2, 358, 4, 4), new Color(1, 1, 1, 0.8f));
        DrawLabel(new Rect(0, 676, vw, 30), "Follow the golden light to the exit", 22, new Color(1, 1, 1, 0.85f));
    }
}

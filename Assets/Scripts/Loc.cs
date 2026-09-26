using System;
using System.Collections.Generic;
using UnityEngine;

public enum Lang { En, Es }

/// <summary>
/// Tiny localization layer: one string table per language, keys, string.Format args.
/// Spanish is neutral Latin American (tu). Character and track names are proper nouns and are
/// never translated. The PC language defaults to the system language (Spanish -> es, else en),
/// is persisted in PlayerPrefs and raises Changed so the UI re-renders live.
/// </summary>
public static class Loc
{
    const string PrefKey = "kart.language";

    public static Lang Current { get; private set; } = Lang.En;
    public static string Code => Current == Lang.Es ? "es" : "en";
    public static event Action Changed;
    static bool initialized;

    /// <summary>Reads PlayerPrefs, or the system language the first time.</summary>
    public static void Init()
    {
        if (initialized) return;
        initialized = true;
        string saved = PlayerPrefs.GetString(PrefKey, "");
        if (saved == "es") Current = Lang.Es;
        else if (saved == "en") Current = Lang.En;
        else Current = Application.systemLanguage == SystemLanguage.Spanish ? Lang.Es : Lang.En;
    }

    public static void Set(Lang lang, bool persist = true)
    {
        initialized = true;
        if (persist)
        {
            PlayerPrefs.SetString(PrefKey, lang == Lang.Es ? "es" : "en");
            PlayerPrefs.Save();
        }
        if (lang == Current) return;
        Current = lang;
        Changed?.Invoke();
    }

    public static Lang Parse(string code) => code == "es" ? Lang.Es : Lang.En;

    public static string T(string key, params object[] args)
    {
        var table = Current == Lang.Es ? Es : En;
        if (!table.TryGetValue(key, out string s) && !En.TryGetValue(key, out s)) s = key;
        return args != null && args.Length > 0 ? string.Format(s, args) : s;
    }

    /// <summary>English 1st/2nd/3rd, Spanish 1.º/2.º/3.º.</summary>
    public static string Ordinal(int n) => n + OrdinalSuffix(n);

    public static string OrdinalSuffix(int n)
    {
        if (Current == Lang.Es) return ".º";
        if (n % 100 >= 11 && n % 100 <= 13) return "th";
        switch (n % 10)
        {
            case 1: return "st";
            case 2: return "nd";
            case 3: return "rd";
            default: return "th";
        }
    }

    // All keys exist in both tables (checked by RaceSelfTests.LocSelfTest).
    public static readonly Dictionary<string, string> En = new Dictionary<string, string>
    {
        ["intro.press"] = "PRESS ANY KEY",
        ["lobby.scan"] = "SCAN TO JOIN",
        ["lobby.wifi"] = "Same Wi-Fi as this PC. Turn the phone sideways.",
        ["lobby.other"] = "Other addresses: {0}",
        ["lobby.server_off"] = "Phone server not running:\n{0}",
        ["lobby.keys1"] = "KEYBOARD: [ENTER] JOIN/READY  [LEFT/RIGHT] PICK  [Q/E] TRACK  [C] CPU RACERS  [L] LANGUAGE  [SPACE] START  [BACKSPACE] LEAVE  [ESC] QUIT",
        ["lobby.keys2"] = "DRIVE: WASD/ARROWS  SPACE TAP = HOP, HOLD+STEER = DRIFT  E/SHIFT = ITEM  ESC/P = PAUSE  [M] MUSIC  [F2] LOOK",
        ["lobby.track"] = "TRACK",
        ["lobby.track_keys"] = "{0}/{1}   < [Q]  [E] >",
        ["lobby.random"] = "RANDOM",
        ["lobby.random_info"] = "Any of the tracks",
        ["lobby.random_twist"] = "Picked when the race starts.",
        ["lobby.track_info"] = "{0} m  -  {1} laps",
        ["lobby.cpu"] = "CPU RACERS: {0}   [C]",
        ["lobby.lang"] = "LANGUAGE: ENGLISH   [L]",
        ["lobby.waiting"] = "WAITING FOR PLAYERS...",
        ["lobby.ready_status"] = "READY {0}/{1}  -  THE RACE STARTS WHEN EVERYONE IS READY",
        ["lobby.empty"] = "Open the URL on a phone\nto join",
        ["lobby.keyboard"] = "Keyboard",
        ["lobby.phone"] = "Phone",
        ["lobby.phone_lost"] = "Phone (reconnecting...)",
        ["lobby.ready"] = "READY!",
        ["stat.speed"] = "SPEED",
        ["stat.accel"] = "ACCEL",
        ["stat.handling"] = "HANDLING",
        ["cpu.fill"] = "FILL TO 6",
        ["cpu.off"] = "OFF",
        ["quit.title"] = "QUIT GAME?",
        ["quit.lobby_hint"] = "[ENTER] YES, QUIT        [ESC] NO, BACK TO THE LOBBY",
        ["pause.title"] = "PAUSED",
        ["pause.by"] = "PAUSED BY {0}",
        ["pause.host"] = "HOST",
        ["pause.resume"] = "RESUME",
        ["pause.restart"] = "RESTART RACE",
        ["pause.lobby"] = "BACK TO LOBBY",
        ["pause.quit"] = "QUIT GAME",
        ["pause.no"] = "NO, GO BACK",
        ["pause.yes"] = "YES, QUIT",
        ["pause.closes"] = "The game will close.",
        ["pause.hint"] = "[UP/DOWN] CHOOSE   [ENTER] SELECT   [ESC] {0}\nor use the menu on the leader's phone",
        ["pause.hint_back"] = "BACK",
        ["pause.hint_resume"] = "RESUME",
        ["fly.laps"] = "{0} LAPS  -  {1} M",
        ["fly.skip"] = "ANY KEY / LEADER'S PHONE: SKIP",
        ["race.go"] = "GO!",
        ["race.lap"] = "LAP {0}/{1}",
        ["race.lap_banner"] = "LAP {0}",
        ["race.final_lap"] = "FINAL LAP!",
        ["race.wrong_way"] = "WRONG WAY!",
        ["race.finished"] = "FINISHED {0}!",
        ["race.speed"] = "{0} KM/H",
        ["race.turbo"] = "TURBO!",
        ["race.drift"] = "DRIFT *",
        ["race.item"] = "ITEM",
        ["race.cpu"] = "CPU",
        ["item.banana"] = "BANANA",
        ["item.turbo"] = "TURBO",
        ["item.rocket"] = "ROCKET",
        ["item.shield"] = "SHIELD",
        ["results.title"] = "RESULTS",
        ["results.prompt"] = "START on the leader's phone or [Enter]: back to the lobby   -   [Esc] pause menu",
        ["results.best"] = "best {0}",
        ["results.winner"] = "WINNER\n{0}",
        ["results.time_trial"] = "TIME TRIAL\n{0}",
        ["twist.night"] = "Floodlit toy circuit: hairpin, S-bends, a hill and a sliding block",
        ["twist.sunset"] = "Wide and fast: long straights, sweepers, a hairpin to drift and a chicane to brake for",
        ["twist.toybox"] = "Two storeys of track: a climbing corner and crests that throw you in the air",
        ["twist.neon"] = "Narrow and technical: esses, a zig-zag chicane and neon blocks sliding across the road",
    };

    public static readonly Dictionary<string, string> Es = new Dictionary<string, string>
    {
        ["intro.press"] = "PRESIONA CUALQUIER TECLA",
        ["lobby.scan"] = "ESCANEA PARA UNIRTE",
        ["lobby.wifi"] = "Misma red Wi-Fi que esta PC. Gira el teléfono.",
        ["lobby.other"] = "Otras direcciones: {0}",
        ["lobby.server_off"] = "El servidor para teléfonos no está activo:\n{0}",
        ["lobby.keys1"] = "TECLADO: [ENTER] UNIRSE/LISTO  [IZQ/DER] ELEGIR  [Q/E] PISTA  [C] CPU  [L] IDIOMA  [ESPACIO] EMPEZAR  [RETROCESO] SALIR  [ESC] CERRAR",
        ["lobby.keys2"] = "CONDUCIR: WASD/FLECHAS  ESPACIO = SALTO, MANTENER+GIRAR = DERRAPE  E/SHIFT = OBJETO  ESC/P = PAUSA  [M] MÚSICA  [F2] ESTILO",
        ["lobby.track"] = "PISTA",
        ["lobby.track_keys"] = "{0}/{1}   < [Q]  [E] >",
        ["lobby.random"] = "ALEATORIA",
        ["lobby.random_info"] = "Cualquiera de las pistas",
        ["lobby.random_twist"] = "Se elige al empezar la carrera.",
        ["lobby.track_info"] = "{0} m  -  {1} vueltas",
        ["lobby.cpu"] = "CORREDORES CPU: {0}   [C]",
        ["lobby.lang"] = "IDIOMA: ESPAÑOL   [L]",
        ["lobby.waiting"] = "ESPERANDO JUGADORES...",
        ["lobby.ready_status"] = "LISTOS {0}/{1}  -  LA CARRERA EMPIEZA CUANDO TODOS ESTÉN LISTOS",
        ["lobby.empty"] = "Abre la dirección en un\nteléfono para unirte",
        ["lobby.keyboard"] = "Teclado",
        ["lobby.phone"] = "Teléfono",
        ["lobby.phone_lost"] = "Teléfono (reconectando...)",
        ["lobby.ready"] = "¡LISTO!",
        ["stat.speed"] = "VELOCIDAD",
        ["stat.accel"] = "ACELERACIÓN",
        ["stat.handling"] = "MANEJO",
        ["cpu.fill"] = "LLENAR A 6",
        ["cpu.off"] = "NO",
        ["quit.title"] = "¿SALIR DEL JUEGO?",
        ["quit.lobby_hint"] = "[ENTER] SÍ, SALIR        [ESC] NO, VOLVER AL LOBBY",
        ["pause.title"] = "PAUSA",
        ["pause.by"] = "PAUSA DE {0}",
        ["pause.host"] = "ANFITRIÓN",
        ["pause.resume"] = "CONTINUAR",
        ["pause.restart"] = "REINICIAR CARRERA",
        ["pause.lobby"] = "VOLVER AL LOBBY",
        ["pause.quit"] = "SALIR DEL JUEGO",
        ["pause.no"] = "NO, VOLVER",
        ["pause.yes"] = "SÍ, SALIR",
        ["pause.closes"] = "El juego se cerrará.",
        ["pause.hint"] = "[ARRIBA/ABAJO] ELEGIR   [ENTER] ACEPTAR   [ESC] {0}\no usa el menú en el teléfono del líder",
        ["pause.hint_back"] = "VOLVER",
        ["pause.hint_resume"] = "CONTINUAR",
        ["fly.laps"] = "{0} VUELTAS  -  {1} M",
        ["fly.skip"] = "CUALQUIER TECLA / TELÉFONO DEL LÍDER: SALTAR",
        ["race.go"] = "¡YA!",
        ["race.lap"] = "VUELTA {0}/{1}",
        ["race.lap_banner"] = "VUELTA {0}",
        ["race.final_lap"] = "¡ÚLTIMA VUELTA!",
        ["race.wrong_way"] = "¡SENTIDO CONTRARIO!",
        ["race.finished"] = "¡TERMINASTE {0}!",
        ["race.speed"] = "{0} KM/H",
        ["race.turbo"] = "¡TURBO!",
        ["race.drift"] = "DERRAPE *",
        ["race.item"] = "OBJETO",
        ["race.cpu"] = "CPU",
        ["item.banana"] = "PLÁTANO",
        ["item.turbo"] = "TURBO",
        ["item.rocket"] = "COHETE",
        ["item.shield"] = "ESCUDO",
        ["results.title"] = "RESULTADOS",
        ["results.prompt"] = "EMPEZAR en el teléfono del líder o [Enter]: volver al lobby   -   [Esc] menú de pausa",
        ["results.best"] = "mejor {0}",
        ["results.winner"] = "GANADOR\n{0}",
        ["results.time_trial"] = "CONTRARRELOJ\n{0}",
        ["twist.night"] = "Circuito de juguete iluminado: horquilla, eses, una colina y un bloque deslizante",
        ["twist.sunset"] = "Ancho y rápido: rectas largas, curvas amplias, una horquilla para derrapar y una chicana para frenar",
        ["twist.toybox"] = "Pista de dos pisos: una curva en subida y crestas que te lanzan por el aire",
        ["twist.neon"] = "Estrecho y técnico: eses, una chicana en zigzag y bloques de neón que cruzan la pista",
    };
}

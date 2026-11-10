import { initialize as initializeTheme, setMode } from "../Soenneker.Quark.Suite/js/themeinterop.js";

export function initialize() {
    let useDarkDefault = false;
    try {
        if (!localStorage.getItem("flywheel-theme-initialized")) {
            const preference = localStorage.getItem("quark-theme");
            useDarkDefault = preference !== "light" && preference !== "dark";
            // System mode clears quark-theme, so remember that the default was already applied.
            localStorage.setItem("flywheel-theme-initialized", "true");
        }
    } catch {
        useDarkDefault = true;
    }

    return useDarkDefault ? setMode("dark") : initializeTheme();
}

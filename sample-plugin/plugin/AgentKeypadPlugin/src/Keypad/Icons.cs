namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Collections.Generic;

    // Stroke icons drawn at runtime as SVG. Paths match the accepted mockup (design/keypad/v13).
    internal static class Icons
    {
        private static readonly Dictionary<String, String> Paths = new()
        {
            ["bell"] = "<path d=\"M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9\"/><path d=\"M10.3 21a1.94 1.94 0 0 0 3.4 0\"/>",
            ["codex"] = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"3\"/><path d=\"m7.5 10 3 2-3 2\"/><path d=\"M12.5 15h4\"/>",
            ["claude"] = "<path d=\"M12 3.5v17M3.5 12h17M6 6l12 12M18 6 6 18\"/>",
            ["area"] = "<path d=\"M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3\"/><circle cx=\"12\" cy=\"12\" r=\"2.4\"/>",
            ["mic"] = "<rect x=\"9\" y=\"3\" width=\"6\" height=\"11\" rx=\"3\"/><path d=\"M5 11a7 7 0 0 0 14 0M12 18v3\"/>",
            ["x"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m9 9 6 6M15 9l-6 6\"/>",
            ["dia"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18\"/>",
            ["back"] = "<path d=\"M9 14 4 9l5-5\"/><path d=\"M4 9h11a5 5 0 0 1 0 10h-3\"/>",
            ["result"] = "<path d=\"M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z\"/><path d=\"M14 3v5h5\"/><path d=\"m9 14 2 2 4-4\"/>",
            ["home"] = "<path d=\"M3 11 12 4l9 7\"/><path d=\"M5 10v10h14V10\"/>",
            ["check"] = "<path d=\"m5 12 5 5 9-10\"/>",
            ["clip"] = "<rect x=\"8\" y=\"3\" width=\"8\" height=\"4\" rx=\"1\"/><path d=\"M8 5H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-2\"/>",
            ["bookmark"] = "<path d=\"M6 3h12v18l-6-4-6 4z\"/><path d=\"M12 7v6M9 10h6\"/>",
            ["gauge"] = "<path d=\"M4 18a9 9 0 1 1 16 0\"/><path d=\"m12 13 4-4\"/>",
            ["smile"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M8.5 14a4.5 4.5 0 0 0 7 0\"/><path d=\"M9 9.5h.01M15 9.5h.01\"/>",
            ["camera"] = "<path d=\"M4 8h3l2-3h6l2 3h3v11H4z\"/><circle cx=\"12\" cy=\"13\" r=\"3.5\"/>",
            ["download"] = "<path d=\"M12 4v11M7 10l5 5 5-5\"/><path d=\"M5 20h14\"/>",
            ["stop"] = "<rect x=\"7\" y=\"7\" width=\"10\" height=\"10\" rx=\"1.5\"/>",
            ["spin"] = "<path d=\"M12 3a9 9 0 1 0 9 9\"/>",
            ["toapp"] = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"3\"/><path d=\"M3 9h18\"/><path d=\"M9 15h6M13 12.5l2.5 2.5-2.5 2.5\"/>",
            ["shield"] = "<path d=\"M12 3l7 3v6c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6z\"/><path d=\"M12 9v4M12 16v.01\"/>",
            ["question"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.4M12 17v.01\"/>",
            ["checkcircle"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m8 12 3 3 5-6\"/>",
            ["next"] = "<path d=\"M4 12h15\"/><path d=\"m14 7 5 5-5 5\"/>",
            ["more"] = "<circle cx=\"6\" cy=\"12\" r=\"1.6\"/><circle cx=\"12\" cy=\"12\" r=\"1.6\"/><circle cx=\"18\" cy=\"12\" r=\"1.6\"/>",
            ["swap"] = "<path d=\"M4 8h15\"/><path d=\"m15 4 4 4-4 4\"/><path d=\"M20 16H5\"/><path d=\"m9 12-4 4 4 4\"/>",
            ["folder"] = "<path d=\"M3 6a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z\"/>",
        };

        // The keypad's SVG renderer ignores stroke settings inherited from the outer element,
        // so each shape carries its own colour and stroke.
        public static String Svg(String name, String color, Int32 size)
        {
            if (!Paths.TryGetValue(name, out var body))
            {
                return null;
            }

            var style = $"fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"";
            body = body.Replace("<path ", $"<path {style} ").Replace("<rect ", $"<rect {style} ").Replace("<circle ", $"<circle {style} ");
            // A near-invisible dot in a second colour stops the renderer treating the icon as single-colour and recolouring it white.
            const String secondColour = "<circle cx=\"0.2\" cy=\"0.2\" r=\"0.15\" fill=\"#010203\" fill-opacity=\"0.02\"/>";
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\">{secondColour}{body}</svg>";
        }
    }
}

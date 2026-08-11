# Megapixel Resolution

Adds an optional **Megapixels** slider immediately below **Aspect Ratio** in
SwarmUI's Resolution group. Drag the slider or enter a value such as `0.4` and
the width and height are selected automatically for the current aspect ratio.

The control uses the selected model's resolution precision and shows the final
rounded dimensions in SwarmUI's existing Resolution header. Adjusting Width,
Height, or Side Length turns Megapixels off, so the most recently edited sizing
mode always wins.

A responsive diagram below the controls shows the selected aspect, exact width,
height, total pixel count, and actual rounded megapixel count. It updates one
second after resolution editing stops, keeping slider and typing interactions
steady.

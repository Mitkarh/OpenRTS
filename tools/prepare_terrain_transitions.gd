extends SceneTree
## Build the two authoring projections and the runtime textures from the existing terrain art.

const SIDE := 64
const COLUMNS := 16
const GUTTER := 2
const ROOT := "res://resources/terrain/"
const CORNERS := ["top_left_corner", "top_right_corner", "bottom_right_corner", "bottom_left_corner"]
const DIAMOND_CORNERS := ["top_corner", "right_corner", "bottom_corner", "left_corner"]
const TERRAIN_SWATCHES := {
	"water": "Color(0.25, 0.48, 0.68, 1)",
	"grass": "Color(0.33, 0.5, 0.25, 1)",
	"mud": "Color(0.55, 0.37, 0.23, 1)",
	"concrete": "Color(0.61, 0.60, 0.56, 1)",
}
var first := "grass"
var second := "mud"
var terrain_id := "grass"
var prefix := "grass-mud"
var angular := false

func _initialize():
	var args := OS.get_cmdline_user_args()
	var style := args.find("--angular")
	if style >= 0:
		angular = true
		args.remove_at(style)
	var id := RegEx.create_from_string("^[a-z][a-z0-9-]*$")
	if args.size() == 1 or args.size() > 3:
		push_error("Usage: prepare_terrain_transitions.gd -- [--angular] [first-texture second-texture [terrain-id]]")
		quit(1)
		return
	if not args.is_empty():
		first = args[0]
		second = args[1]
		terrain_id = args[2] if args.size() == 3 else first
	if first == second or id.search(first) == null or id.search(second) == null or id.search(terrain_id) == null:
		push_error("Specify two distinct texture IDs and an optional gameplay terrain ID.")
		quit(1)
		return
	prefix = first + "-" + second
	var first_image := load_ground(first)
	var second_image := load_ground(second)
	if first_image == null or second_image == null or not FileAccess.file_exists(ROOT + first + ".tres") or not FileAccess.file_exists(ROOT + second + ".tres"):
		push_error("Each source texture needs an assets/terrain PNG and a resources/terrain texture resource.")
		quit(1)
		return

	var ground: Array[Image] = []
	for index in range(36):
		ground.append(blend_tile(first_image, second_image, index))
	var include_road := prefix == "grass-mud"
	if include_road:
		ground.append(load_ground("road"))
	var square_size := Vector2i(SIDE, SIDE)
	var diamond_size := Vector2i(SIDE, SIDE / 2)
	var square := "res://assets/terrain/" + prefix + "-top-down.png"
	var diamond := "res://assets/terrain/" + prefix + "-isometric.png"
	if write_atlas(square, ground, square_size, false) != OK or write_atlas(diamond, ground, diamond_size, true) != OK:
		push_error("Failed to save terrain transition atlases.")
		quit(1)
		return
	var tileset_name := first.capitalize().replace(" ", "") + second.capitalize().replace(" ", "")
	write_tileset(tileset_name + "TopDown", square, square_size, false, ground.size())
	write_tileset(tileset_name + "Isometric", diamond, diamond_size, true, ground.size())
	for index in range(36):
		if index == 0 or index == 31:
			continue
		var origin := tile_origin(index, square_size)
		save_text(ROOT + visual_id(index) + ".tres",
			'[gd_resource type="AtlasTexture" format=3]\n\n[ext_resource type="Texture2D" path="%s" id="1"]\n\n'
			% square + '[resource]\natlas = ExtResource("1")\nregion = Rect2(%d, %d, %d, %d)\nfilter_clip = true\n'
			% [origin.x, origin.y, SIDE, SIDE])
	print("Prepared ", prefix, " transitions with TerrainId ", terrain_id, " for square and Diamond Down tiles.")
	quit()

func load_ground(id: String) -> Image:
	var image := Image.load_from_file(ProjectSettings.globalize_path("res://assets/terrain/" + id + ".png"))
	if image == null or image.is_empty():
		return null
	image.resize(SIDE, SIDE, Image.INTERPOLATE_LANCZOS)
	image.convert(Image.FORMAT_RGBA8)
	return image

func blend_tile(base: Image, overlay: Image, index: int) -> Image:
	var result := Image.create_empty(SIDE, SIDE, false, Image.FORMAT_RGBA8)
	for y in range(SIDE):
		for x in range(SIDE):
			# Sample the actual edge values so adjacent terrain patterns agree along a shared edge.
			var u := float(x) / (SIDE - 1)
			var v := float(y) / (SIDE - 1)
			var field := patch_field(index & 15, index >= 16, u, v) if index < 32 else diagonal_field(index - 32, u, v)
			result.set_pixel(x, y, base.get_pixel(x, y).lerp(overlay.get_pixel(x, y), smoothstep(-0.12, 0.12, field)))
	return result

func patch_field(bits: int, overlay_center: bool, u: float, v: float) -> float:
	var nw := corner_value(bits, 0)
	var ne := corner_value(bits, 1)
	var se := corner_value(bits, 2)
	var sw := corner_value(bits, 3)
	var center := 1.0 if overlay_center else -1.0
	var x := u - 0.5
	var y := v - 0.5
	# The four planes meet on the two diagonals. Keep the original field in a strip along each edge.
	var edge_distance := minf(minf(u, 1.0 - u), minf(v, 1.0 - v))
	var radius := 0.0 if angular else 0.18 * smoothstep(0.08, 0.22, edge_distance)
	return center + (ne + se - nw - sw) * x * 0.5 + (se + sw - nw - ne) * y * 0.5 \
		+ ((ne + sw) * 0.5 - center) * rounded_abs(x - y, radius) \
		+ ((nw + se) * 0.5 - center) * rounded_abs(x + y, radius)

func rounded_abs(value: float, radius: float) -> float:
	var distance := absf(value)
	return distance if distance >= radius else (value * value / radius + radius) * 0.5

func corner_value(bits: int, corner: int) -> float:
	return 1.0 if (bits & (1 << corner)) != 0 else -1.0

func diagonal_field(direction: int, u: float, v: float) -> float:
	match direction:
		0: return u + v - 1.0
		1: return 1.0 - u - v
		2: return u - v
		_: return v - u

func tile_origin(index: int, size: Vector2i) -> Vector2i:
	return Vector2i(index % COLUMNS, index / COLUMNS) * (size + Vector2i.ONE * (2 * GUTTER)) + Vector2i.ONE * GUTTER

func write_atlas(path: String, tiles: Array[Image], size: Vector2i, isometric: bool) -> Error:
	var stride := size + Vector2i.ONE * (2 * GUTTER)
	var atlas := Image.create_empty(stride.x * COLUMNS, stride.y * 3, false, Image.FORMAT_RGBA8)
	for i in range(tiles.size()):
		var tile := project_diamond(tiles[i], size) if isometric else tiles[i]
		var origin := tile_origin(i, size)
		for y in range(-GUTTER, size.y + GUTTER):
			for x in range(-GUTTER, size.x + GUTTER):
				atlas.set_pixelv(origin + Vector2i(x, y), tile.get_pixel(clampi(x, 0, size.x - 1), clampi(y, 0, size.y - 1)))
	return atlas.save_png(path)

func project_diamond(source: Image, size: Vector2i) -> Image:
	var tile := Image.create_empty(size.x, size.y, false, Image.FORMAT_RGBA8)
	for y in range(size.y):
		for x in range(size.x):
			var horizontal := (x + 0.5) / size.x - 0.5
			var vertical := (y + 0.5) / size.y
			var u := vertical + horizontal
			var v := vertical - horizontal
			if u >= 0.0 and u < 1.0 and v >= 0.0 and v < 1.0:
				tile.set_pixel(x, y, source.get_pixel(int(u * SIDE), int(v * SIDE)))
	return tile

func visual_id(index: int) -> String:
	if index == 0: return first
	if index == 31: return second
	if index == 36: return "road"
	return "%s-%02d" % [prefix, index]

func write_tileset(file: String, texture: String, size: Vector2i, isometric: bool, count: int):
	var text := '[gd_resource type="TileSet" format=3]\n\n[ext_resource type="Texture2D" path="%s" id="1"]\n\n' % texture
	text += '[sub_resource type="TileSetAtlasSource" id="Atlas"]\ntexture = ExtResource("1")\n'
	text += "margins = Vector2i(%d, %d)\nseparation = Vector2i(%d, %d)\ntexture_region_size = Vector2i(%d, %d)\n" % [
		GUTTER, GUTTER, 2 * GUTTER, 2 * GUTTER, size.x, size.y]
	var corners := DIAMOND_CORNERS if isometric else CORNERS
	for index in range(count):
		var tile := "%d:%d/0" % [index % COLUMNS, index / COLUMNS]
		var visual := visual_id(index)
		text += "%s = 0\n%s/custom_data_0 = \"%s\"\n%s/custom_data_1 = \"%s\"\n" % [
			tile, tile, "road" if index == 36 else terrain_id, tile, visual]
		if index != 0 and index != 31 and index != 36:
			text += "%s/custom_data_2 = true\n" % tile
		if index < 32:
			text += "%s/terrain_set = 0\n%s/terrain = %d\n" % [tile, tile, index / 16]
			for corner in range(4):
				text += "%s/terrains_peering_bit/%s = %d\n" % [tile, corners[corner], (index >> corner) & 1]
	text += "\n[resource]\n"
	if isometric:
		text += "tile_shape = 1\ntile_layout = 5\n"
	text += "tile_size = Vector2i(%d, %d)\n" % [size.x, size.y]
	text += 'terrain_set_0/mode = 1\nterrain_set_0/terrain_0/name = "%s"\nterrain_set_0/terrain_0/color = %s\n' % [
		first.capitalize(), TERRAIN_SWATCHES.get(first, TERRAIN_SWATCHES["grass"])]
	text += 'terrain_set_0/terrain_1/name = "%s (visual)"\nterrain_set_0/terrain_1/color = %s\n' % [
		"Dirt" if second == "mud" else second.capitalize(), TERRAIN_SWATCHES.get(second, TERRAIN_SWATCHES["mud"])]
	text += 'custom_data_layer_0/name = "TerrainId"\ncustom_data_layer_0/type = 4\ncustom_data_layer_1/name = "VisualId"\ncustom_data_layer_1/type = 4\n'
	text += 'custom_data_layer_2/name = "DirectionalVisual"\ncustom_data_layer_2/type = 1\nsources/0 = SubResource("Atlas")\n'
	save_text(ROOT + file + ".tres", text)

func save_text(path: String, text: String):
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)

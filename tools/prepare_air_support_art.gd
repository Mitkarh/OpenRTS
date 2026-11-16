extends SceneTree

# Normalize generated source images without changing artwork or alpha.
func _init():
	var root = ProjectSettings.globalize_path("res://").path_join("..").simplify_path()
	var manifest = root.path_join("docs/assets/air-support-art.json")
	var data = JSON.parse_string(FileAccess.get_file_as_string(manifest))
	for entry in data.assets:
		var source = root.path_join(entry.source)
		var digest = FileAccess.get_sha256(source)
		assert(not entry.has("source_sha256") or entry.source_sha256 == digest, "Changed source: " + source)
		var image = Image.load_from_file(source)
		assert(image != null and not image.is_empty() and image.detect_alpha() != Image.ALPHA_NONE)
		var bounds = image.get_used_rect()
		var crop = image.get_region(bounds)
		var scale = 928.0 / max(crop.get_width(), crop.get_height())
		crop.resize(roundi(crop.get_width() * scale), roundi(crop.get_height() * scale), Image.INTERPOLATE_LANCZOS)
		var canvas = Image.create_empty(1024, 1024, false, Image.FORMAT_RGBA8)
		canvas.blit_rect(crop, Rect2i(Vector2i.ZERO, crop.get_size()), (Vector2i(1024, 1024) - crop.get_size()) / 2)
		var destination = root.path_join(entry.asset)
		assert(canvas.save_png(destination) == OK)
		entry.source_sha256 = digest
		entry.sha256 = FileAccess.get_sha256(destination)
		entry.normalization = "alpha>0 bounds, aspect fit928 centered1024, preserve alpha"
		var resource = root.path_join("sandbox/resources/units/" + entry.name + ".tres")
		var animations = []
		for sequence in ["stand", "travel", "shoot"]:
			animations.append('{"frames": [{"duration": 1.0, "texture": ExtResource("1")}], "loop": true, "name": &"' + sequence + '", "speed": 1.0}')
		var file = FileAccess.open(resource, FileAccess.WRITE)
		file.store_string('[gd_resource type="SpriteFrames" load_steps=2 format=3]\n\n[ext_resource type="Texture2D" path="res://assets/units/' + entry.name + '.png" id="1"]\n\n[resource]\nanimations = [' + ',\n'.join(animations) + ']\n')
	var file = FileAccess.open(manifest, FileAccess.WRITE)
	file.store_string(JSON.stringify(data, "  ") + "\n")
	print("Prepared ", data.assets.size(), " air support sprites")
	quit()

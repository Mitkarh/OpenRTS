extends Node3D

@export var bunker_turret_scene: PackedScene

@onready var dish: Node3D = $Radar.find_child("DishYaw", true, false)
@onready var arm: Node3D = $ConstructionCrane.find_child("CraneYaw", true, false)
@onready var hook: Node3D = arm.find_child("HookLift", true, false)
@onready var hook_rest: float = hook.position.y
@onready var bunker_mount: Node3D = $BunkerArmed.find_child("TurretMount", true, false)
@onready var stealth: Node3D = $StealthGenerator.find_child("StealthYaw", true, false)

var elapsed: float = 0.0

func _ready() -> void:
    bunker_mount.add_child(bunker_turret_scene.instantiate())

func _process(delta: float) -> void:
    elapsed += delta
    dish.rotate_y(-deg_to_rad(65.0) * delta)
    arm.rotation.y = sin(elapsed * 1.05) * 0.88
    hook.position.y = hook_rest - (0.5 - 0.5 * cos(elapsed * 3.3)) * 0.19
    bunker_mount.rotation.y = sin(elapsed * 0.8) * 1.15
    stealth.rotate_y(-deg_to_rad(140.0) * delta)

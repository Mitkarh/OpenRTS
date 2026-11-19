extends Node3D

@export var bunker_turret_scene: PackedScene

@onready var dish: Node3D = $Radar.find_child("DishYaw", true, false)
@onready var arm: Node3D = $ConstructionCrane.find_child("CraneYaw", true, false)
@onready var hook: Node3D = arm.find_child("HookLift", true, false)
@onready var hook_rest: float = hook.position.y
@onready var bunker_mount: Node3D = $BunkerArmed.find_child("TurretMount", true, false)
@onready var stealth: Node3D = $StealthGenerator.find_child("StealthYaw", true, false)
@onready var tank_turret: Node3D = $Tank.find_child("TurretSocket", true, false)
@onready var apc_turret: Node3D = $APC.find_child("TurretSocket", true, false)
@onready var truck_crane: Node3D = $Engineer.find_child("TurretSocket", true, false)
@onready var salvo_pods: Node3D = $MLRS.find_child("TurretSocket", true, false)
@onready var cruise_rail: Node3D = $Tomahawk.find_child("TurretSocket", true, false)
@onready var mobile_emitter: Node3D = $StealthTruck.find_child("EmitterRotor", true, false)
@onready var cruise_missile: Node3D = $Tomahawk.find_child("ReadyMissile", true, false)
@onready var scud_missile: Node3D = $Scud.find_child("ReadyMissile", true, false)
@onready var cruise_rest: Vector3 = cruise_missile.position
@onready var scud_rest: Vector3 = scud_missile.position

var elapsed: float = 0.0

func _ready() -> void:
    bunker_mount.add_child(bunker_turret_scene.instantiate())
    truck_crane.rotation.y = PI

func _process(delta: float) -> void:
    elapsed += delta
    dish.rotate_y(-deg_to_rad(65.0) * delta)
    arm.rotation.y = sin(elapsed * 1.05) * 0.88
    hook.position.y = hook_rest - (0.5 - 0.5 * cos(elapsed * 3.3)) * 0.19
    bunker_mount.rotation.y = sin(elapsed * 0.8) * 1.15
    stealth.rotate_y(-deg_to_rad(140.0) * delta)
    tank_turret.rotation.y = sin(elapsed * 0.7) * 0.9
    apc_turret.rotation.y = sin(elapsed * 0.9) * 0.75
    truck_crane.rotation.y = PI + sin(elapsed * 0.85) * 0.8
    salvo_pods.rotation.y = sin(elapsed * 0.7) * 0.55
    cruise_rail.rotation.y = sin(elapsed * 0.8) * 0.5
    mobile_emitter.rotate_y(-deg_to_rad(140.0) * delta)
    demo_launch(cruise_missile, cruise_rest, 0.76, 18.0, elapsed)
    demo_launch(scud_missile, scud_rest, 0.94, 82.0, elapsed - 1.5)

func demo_launch(missile: Node3D, rest: Vector3, half_length: float, degrees: float, time: float) -> void:
    var cycle := fposmod(time, 10.0)
    var pitch := deg_to_rad(degrees) * smoothstep(3.0, 4.0, cycle)
    var travel: float = maxf(0.0, cycle - 4.0) * 6.0
    var forward := Vector3.FORWARD.rotated(Vector3.RIGHT, pitch)
    missile.visible = cycle < 5.5
    missile.rotation.x = pitch
    missile.position = rest + Vector3.BACK * half_length + forward * (half_length + travel)

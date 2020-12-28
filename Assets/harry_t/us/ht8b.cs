/* 
 https://www.harrygodden.com

 live:	wrld_08badc69-7665-4dc5-8243-3867455dc17c
 dev:		wrld_9497c2da-97ee-4b2e-9f82-f9adb024b6fe

 Update log:
	16.12.2020 (0.1.3a)	-	Fix for new game, wrong local turn colour
								-	Fix for not losing match when scratch on pot 8 ball				( Thanks: Photographotter, Mystical )
								-	Added permission info to console when fail reset
	17.12.2020 (0.2.0a)	-	Predictive physics for cue ball
								-  Fix for not winning when sink 8, and objective on same turn		( Thanks: Rosy )
								-	Reduced code spaghet in decode routine
								-	Improved algorithm for post-game state checking, should lend
								   to easier implementation of optional rules.
								-	Allow colour switching between UK/USA/Default colour sets
								-  Grips change colour based on which turn it is
					0.3.0a	-	Added desktop mode
								-	Sink opponents ball = loss turn
								-	Removed coloursets
					0.3.1a	-	Desktop QOL
					0.3.2a	-	Reduced sensitivity
								-	Added pad bytes
					0.3.7a	-	Quest support
					0.3.8a	-	Switched network string to base64 encoded
								-	Changed initial break setup

 Networking Model Information:
	
	This implementation of 8 ball is based around passing ownership between clients who are
	playing the game. A player is 'registered' into the game when they have ownership of one
	of the two player 'totems'. In this implementation the totems are the pool cues themselves.

	When a turn ends, the player who is currently playing will pack information into the 
	networking string that the turn has been transferred, and once the remote client who is
	associated with the opposite cue recieves the update, they will take ownership of the main
	script.

	The local player will have a 'permit' to shoot when it is their turn, which allows them
	to interact with the physics world. As soon as the cue ball is shot, the script calculates
	and compresses the necessery velocities and positions of the balls, and 1. sends that out
	to remote clients, and 2. decodes it the same way themselves. So effectively all players
	end up watching the exact same simulation at very close to the same time. In testing this
	was immediate as it could be with a GB -> USA connection.

 Information about the data:

	- Data is transfered using 1 Udon Synced string which is 82 bytes long, encoded to base64( 110 bytes )
	- Critical game states are packed into a bitmask at #19
	- Floating point positions are encoded/decoded as follows:
		Encode:
			Divide the value by the expected maximum range
			Multiply that by signed short max value ~32k
			Add signed short max
			Cast to ushort
		Decode:
			Cast ushort to float
			Subtract short max
			Divide by short max
			Multiply by the same range encoded with

	- Ball ID's are designed around bitmasks and are as follows:

	byte | Byte 0														| Byte 1														|
	bit  | x80 . x40 . x20 . x10 . x08 . x04 . x02	| x1 .. x80 . x40 . x20 . x10 . x08 . x04 | x02 | x01 |
	ball | 15	 14	 13    12    11    10    9    |  7     6     5     4     3    2     1   |  8  | cue |

 Networking Layout:

   Total size: 78 bytes over network // 39 C# wchar
 
   Address		What						Data type
  
	[ 0x00  ]	ball positions			(compressed quantized vec2's)
	[ 0x40  ]	cue ball velocity		^
	[ 0x44  ]	cue ball angular vel	^

	[ 0x48  ]	sn_pocketed				uint16 bitmask ( above table )
	
	[ 0x4A  ]	game state flags		| bit #	| mask	| what				| 
												| 0		| 0x1		| sn_simulating	|
												| 1		| 0x2		| sn_turnid			|
												| 2		| 0x4		| sn_foul			|
												| 3		| 0x8		| sn_open			|
												| 4		| 0x10	| sn_playerxor		|
												| 5		| 0x20	| sn_gameover		|
												| 6		| 0x40	| sn_winnerid		|
												| 7		| 0x80	| sn_permit			|
												
	[ 0x4C  ]	packet #					uint16
	[ 0x4E  ]	gameid					uint16
	[ 0x50  ]	colourset id			uint16

 Physics Implementation:
	
	Physics are done in 2D to save instructions. The implementation is designed to be
	as numerically stable as possible (eg. using linear algebra as much as possible to
	be explicit about what and where stuff collides ).

	Ball physic response is 100% pure elastic energy transfer, which even at one iteration
	per physics update seems to give plausable enough results. balls can behave like a 
	newtons cradle which is what we want.

	Edge collisions are a little contrived and the reason why the table can ONLY be placed
	at world orign. the table is divided into major and minor sections. some of the 
	calculations can be peeked at here: https://www.geogebra.org/m/jcteyvj6 . It is all
	straight line equations.
	
	There MAY be deviations between SOME client cpus / platforms depending on the floating 
	point architecture, and who knows what the fuck C# will decide to do at runtime anyway. 
	However after some testing this seems rare enough that we could not observe any
	differences at all. If it does happen to be calculated differently, the remote clients
	will catch up with the players game anyway. I reckon this is most likely going to
	affect, if it does at all, only quest/pc crossplay and not much else.

	Physics are calculated on a fixed timestep, using accumulator model. If there is very
	low framerate physics may run at a slower timescale if it passes the threshold where
	maximum updates/frame is reached, but won't affect eventual outcome.
	
	The display balls have their position matched, and rotated based on pure rolling model.
*/

// https://feedback.vrchat.com/feature-requests/p/udon-expose-shaderpropertytoid
// #define USE_INT_UNIFORMS

// Currently unstable..
// #define HT8B_ALLOW_AUTOSWITCH

#if !UNITY_ANDROID
#define HT8B_DEBUGGER
#else
#define HT_QUEST
#endif

//#define MULTIGAMES_PORTAL

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using System;

public class ht8b : UdonSharpBehaviour {

const string FRP_LOW =	"<color=\"#ADADAD\">";
const string FRP_ERR =	"<color=\"#B84139\">";
const string FRP_WARN = "<color=\"#DEC521\">";
const string FRP_YES =	"<color=\"#69D128\">";
const string FRP_END =	"</color>";

[SerializeField] GameObject[]	balls_render;
[SerializeField] public GameObject cuetip;
[SerializeField] GameObject	guideline;
[SerializeField] GameObject	guidefspin;
[SerializeField] GameObject	devhit;
[SerializeField] Text			ltext;

[SerializeField] Vector2		extraGravy;
[SerializeField] GameObject[] playerTotems;
[SerializeField] GameObject[] cueTips;
[SerializeField] Text[]			playerNames;
[SerializeField] Renderer		scoreCardRenderer;
[SerializeField] GameObject	gametable;
[SerializeField] Renderer		tableRenderer;
[SerializeField] GameObject	infBaseTransform;
[SerializeField] Text			infText;
[SerializeField] GameObject	markerObj;
[SerializeField] Renderer		markerRender;
[SerializeField] GameObject	infHowToStart;
[SerializeField] Renderer[]	cueRenderers;
[SerializeField] Texture[]		textureSets;
[SerializeField] Material		ballMaterial;
[SerializeField] Material[]	CueGripMaterials;
[SerializeField] ht8b_cue[]	gripControllers;
[SerializeField] Material		guidelineMat;

[SerializeField] Transform[]	portalPositions;

// Audio Components
AudioSource aud_main;

[SerializeField] AudioClip		snd_Intro;
[SerializeField] AudioClip		snd_Sink;
[SerializeField] AudioClip[]	snd_Hits;
[SerializeField] AudioClip		snd_NewTurn; 

// REGION GAME STATE
// =========================================================================================================================

[UdonSynced]	private string netstr;		// dumpster fire
					private string netstr_prv;
					byte[]			net_data = new byte[0x52];

// Networked game flags
uint	sn_pocketed		= 0x00U;		// 18 Each bit represents each ball, if it has been pocketed or not

public bool	sn_simulating	= false;		// 19:0 (0x01)		True whilst balls are rolling
uint	sn_turnid		= 0x00U;		// 19:1 (0x02)		Whos turn is it, 0 or 1
bool  sn_foul			= false;		// 19:2 (0x04)		End-of-turn foul marker
bool  sn_open			= true;		// 19:3 (0x08)		Is the table open?
uint  sn_playerxor	= 0x00;		// 19:4 (0x10)		What colour the players have chosen
bool  sn_gameover		= true;		// 19:5 (0x20)		Game is complete
uint  sn_winnerid		= 0x00U;		// 19:6 (0x40)		Who won the game if sn_gameover is set
public bool	sn_permit= false;		// 19:7 (0x80)		Permission for player to play

// Ruleset flags
bool	sn_rs_call8		= false;		//	19:8 (0x100)	Call 8 ball pocket
bool	sn_rs_call		= false;		//	19:9 (0x200)	Call every pocket
bool	sn_rs_anyf		= false;		// 19:10(0x400)	Any ball can be hit first by cue ball

ushort sn_packetid	= 0;			// 20 Current packet number, used for locking updates so we dont accidently go back.
											//    this behaviour was observed on some long connections so its necessary
ushort sn_gameid		= 0;			// 21 Game number

ushort sn_colourid	= 0;			// 22 Colour set ID

// Cannot making a struct in C#, therefore values are duplicated

uint sn_pocketed_prv;
bool sn_simulating_prv;
uint sn_turnid_prv;
bool sn_foul_prv;
bool sn_open_prv;
uint sn_playerxor_prv;
bool sn_gameover_prv;
uint sn_winnerid_prv;
bool sn_permit_prv;
bool sn_rs_call8_prv;
bool sn_rs_call_prv;
bool sn_rs_anyf_prv;
ushort sn_gameid_prv;
ushort sn_colourid_prv;

// Local gamestates
public bool	sn_armed	= false;
bool	sn_updatelock	= false;		// We are waiting for our local simulation to finish, before we unpack data
int	sn_firsthit		= 0;			// The first ball to be hit by cue ball

byte	sn_wins0			= 0;			// Wins for player 0 (unused)
byte	sn_wins1			= 0;			// Wins for player 1 (unused)

float	introAminTimer = 0.0f;		// Ball dropper timer

bool	ballsMoving		= false;		// Tracker variable to see if balls are still on the go

bool	isReposition	= false;			// Repositioner is active
float repoMaxX			= TABLE_WIDTH;	// For clamping to table or set lower for kitchen

// these had to be put up here for some reason
const float FIXED_TIME_STEP = 0.0125f;			// time step in seconds per iteration
const float TIME_ALPHA = 50.0f;					// (unused) physics interpolation

// Physics memory

public Vector2[] ball_co = new Vector2[16];	// Current positions
Vector2[] ball_og = new Vector2[16];	// Break positions
public Vector2[] ball_vl = new Vector2[16];	// Current velocities
Vector2	 cue_avl = Vector2.zero;		// Cue ball angular velocity

// General local aesthetic events
// =========================================================================================================================

Color k_tableColourBlue	= new Color( 0.0f, 0.75f, 1.75f, 1.0f ); // Presets ..
Color k_tableColourOrange = new Color( 1.75f, 0.25f, 0.0f, 1.0f );
Color k_tableColourRed	= new Color( 1.2f, 0.0f, 0.0f, 1.0f );
Color k_tableColorWhite	= new Color( 1.0f, 1.0f, 1.0f, 1.0f );
Color k_tableColourBlack= new Color( 0.04f, 0.04f, 0.04f, 1.0f );
Color k_tableColourYellow = new Color( 2.0f, 1.0f, 0.0f, 1.0f );

Color tableSrcColour		= new Color( 1.0f, 1.0f, 1.0f, 1.0f );	// Runtime target colour
Color tableCurrentColour= new Color( 1.0f, 1.0f, 1.0f, 1.0f );	// Runtime actual colour

Color markerColorOK		= new Color( 0.0f, 1.0f, 0.0f, 1.0f );
Color markerColorNO		= new Color( 1.0f, 0.0f, 0.0f, 1.0f );

Color k_gripColourActive = new Color( 0.0f, 0.5f, 1.1f, 1.0f );
Color k_gripColourInactive = new Color( 0.34f, 0.34f, 0.34f, 1.0f );

// 'Pointer' colours.
Color pColour0;
Color pColour1;
Color pColourErr;

public ushort in_coloursetid = 0;

public void PushColourSet()
{
	if( Networking.GetOwner( playerTotems[0] ) == Networking.LocalPlayer || Networking.GetOwner( playerTotems[1] ) == Networking.LocalPlayer )
	{
		sn_colourid = in_coloursetid;
		UpdateColourSources();
	}
}

public void UpdateColourSources()
{
	ballMaterial.SetTexture( "_MainTex", textureSets[ sn_colourid ] );

	if( sn_colourid == 0 )	// harry_t
	{
		pColour0 = k_tableColourBlue;
		pColour1 = k_tableColourOrange;
		pColourErr = k_tableColourRed;
	}
	else if( sn_colourid == 1 )	// USA
	{
		pColour0 = k_tableColorWhite;
		pColour1 = k_tableColorWhite;
		pColourErr = k_tableColourRed;
	}
	else
	{
		pColour0 = k_tableColourRed;
		pColour1 = k_tableColourYellow;
		pColourErr = k_tableColourBlack;
	}
}

// Shader uniforms
#if USE_INT_UNIFORMS

int uniform_tablecolour;
int uniform_scorecard_colour0;
int uniform_scorecard_colour1;
int uniform_scorecard_info;
int uniform_marker_colour;
int uniform_cue_colour;

#else

const string uniform_tablecolour = "_EmissionColour";
const string uniform_scorecard_colour0 = "_Colour0";
const string uniform_scorecard_colour1 = "_Colour1";
const string uniform_scorecard_info = "_Info";
const string uniform_marker_colour = "_Color";
const string uniform_cue_colour = "_ReColor";

#endif

// Epic crossover portal mode stuff

[SerializeField] Material portal_dispmat_m;

[SerializeField] Material portal_dispmat_0_0;	// Table
[SerializeField] Material portal_dispmat_1_0;
[SerializeField] Material portal_dispmat_0_1;	// Balls
[SerializeField] Material portal_dispmat_1_1;

[SerializeField] Material portal_dispmat_gl0;	// Guideline
[SerializeField] Material portal_dispmat_gl1;

[SerializeField] GameObject portal_ring_0;
[SerializeField] GameObject portal_ring_1;

[SerializeField] bool FORCERANDOM = false;

#if MULTIGAMES_PORTAL

Matrix4x4 m4_portal_0;
Matrix4x4 m4_portal_1;
Matrix4x4 m4_temp_r;
Matrix4x4 m4_temp_t;
Matrix4x4 m4_temp_t1;

float portal_tunnel_r = 0.0f;
Vector2 portal_tunnel_v;

float portal_0_dims_x;
float portal_0_dims_y;
float portal_0_dims_z;
float portal_0_dims_w;

float portal_1_dims_x;
float portal_1_dims_y;
float portal_1_dims_z;
float portal_1_dims_w;

const float portal_radius_sqr = 0.0625f;
float portal_radius = 0.25f;

public void PortalRandomize()
{
	int portalid_0 = UnityEngine.Random.Range(0, portalPositions.Length);
	int portalid_1 = UnityEngine.Random.Range(0, portalPositions.Length);

	if( portalid_0 == portalid_1 )
	{
		portalid_1 ++;

		if( portalid_1 >= portalPositions.Length )
		{
			portalid_1 = 0;
		}
	}

	Transform t0 = portalPositions[ portalid_0 ];
	Transform t1 = portalPositions[ portalid_1 ];

	Vector3 delta = t1.position - t0.position;
	portal_tunnel_v.x = delta.x;
	portal_tunnel_v.y = delta.z;

	float rot_0 = Mathf.Atan2( t0.forward.z, t0.forward.x );
	float rot_1 = Mathf.Atan2( t1.forward.z, t1.forward.x );
	portal_tunnel_r = rot_1 - rot_0 + Mathf.PI;

	m4_temp_r = Matrix4x4.Rotate(Quaternion.AngleAxis( portal_tunnel_r*Mathf.Rad2Deg, Vector3.up ));
	m4_temp_t = Matrix4x4.Translate(-t1.position);
	m4_temp_t1 = Matrix4x4.Translate(t0.position);

	m4_portal_0 = m4_temp_t1 * m4_temp_r * m4_temp_t;

	m4_temp_r = Matrix4x4.Rotate(Quaternion.AngleAxis( -portal_tunnel_r*Mathf.Rad2Deg, Vector3.up ));
	m4_temp_t = Matrix4x4.Translate(-t0.position);
	m4_temp_t1 = Matrix4x4.Translate(t1.position);
	
	m4_portal_1 = m4_temp_t1 * m4_temp_r * m4_temp_t;
	 
	portal_ring_0.transform.position = t0.position;
	portal_ring_0.transform.rotation = t0.rotation;
	portal_ring_1.transform.position = t1.position;
	portal_ring_1.transform.rotation = t1.rotation;

	Vector4 plane0 = new Vector4( t0.forward.x, t0.forward.y, t0.forward.z, Vector3.Dot( t0.forward, t0.position ) );
	Vector4 plane1 = new Vector4( t1.forward.x, t1.forward.y, t1.forward.z, Vector3.Dot( t1.forward, t1.position ) );
	Vector4 px0 = new Vector4( t0.position.x, t0.position.y, t0.position.z, portal_radius_sqr );
	Vector4 px1 = new Vector4( t1.position.x, t1.position.y, t1.position.z, portal_radius_sqr );

	portal_dispmat_0_0.SetMatrix( "_ExtTrf", m4_portal_0 );
	portal_dispmat_0_1.SetMatrix( "_ExtTrf", m4_portal_0 );
	portal_dispmat_1_0.SetMatrix( "_ExtTrf", m4_portal_1 );
	portal_dispmat_1_1.SetMatrix( "_ExtTrf", m4_portal_1 );
	portal_dispmat_gl0.SetMatrix( "_ExtTrf", m4_portal_0 );
	portal_dispmat_gl1.SetMatrix( "_ExtTrf", m4_portal_1 );

	portal_dispmat_0_0.SetVector( "_Portal0", plane0 );
	portal_dispmat_0_0.SetVector( "_Portal0_Pos", px0 );

	portal_dispmat_0_1.SetVector( "_Portal0", plane0 );
	portal_dispmat_0_1.SetVector( "_Portal0_Pos", px0 );

	portal_dispmat_gl0.SetVector( "_Portal0", plane0 );
	portal_dispmat_gl0.SetVector( "_Portal0_Pos", px0 );

	portal_dispmat_1_0.SetVector( "_Portal0", plane1 );
	portal_dispmat_1_0.SetVector( "_Portal0_Pos", px1 );

	portal_dispmat_1_1.SetVector( "_Portal0", plane1 );
	portal_dispmat_1_1.SetVector( "_Portal0_Pos", px1 );

	portal_dispmat_gl1.SetVector( "_Portal0", plane1 );
	portal_dispmat_gl1.SetVector( "_Portal0_Pos", px1 );

	portal_dispmat_m.SetVector( "_Portal0", plane0 );
	portal_dispmat_m.SetVector( "_Portal1", plane1 );
	portal_dispmat_m.SetVector( "_Portal0_Pos", px0 );
	portal_dispmat_m.SetVector( "_Portal1_Pos", px1 );

	// Set lhand position
	portal_0_dims_x = t0.position.x + t0.right.x * portal_radius;
	portal_0_dims_y = t0.position.z + t0.right.z * portal_radius;

	// trace vector
	portal_0_dims_z = t0.right.x * portal_radius * -2.0f;
	portal_0_dims_w = t0.right.z * portal_radius * -2.0f;

	// Set lhand position
	portal_1_dims_x = t1.position.x + t1.right.x * portal_radius;
	portal_1_dims_y = t1.position.z + t1.right.z * portal_radius;
	 
	// trace vector
	portal_1_dims_z = t1.right.x * portal_radius * -2.0f;
	portal_1_dims_w = t1.right.z * portal_radius * -2.0f;

	// Debug.DrawLine( new Vector3(portal_0_dims_x, 0.01f, portal_0_dims_y), new Vector3(portal_0_dims_x+portal_0_dims_z, 0.01f, portal_0_dims_y+portal_0_dims_w), Color.magenta, 10.0f );
	// Debug.DrawLine( new Vector3(portal_1_dims_x, 0.01f, portal_1_dims_y), new Vector3(portal_1_dims_x+portal_1_dims_z, 0.01f, portal_1_dims_y+portal_1_dims_w), Color.yellow, 10.0f );

	Debug.Log( delta.ToString() );

}

void BallPortal( int id )
{
	float cx, cy;
	float c, s;
	float bdp, u;

	float ax = ball_co[id].x;
	float ay = ball_co[id].y;
	float az = ball_vl[id].x * FIXED_TIME_STEP;
	float aw = ball_vl[id].y * FIXED_TIME_STEP;

	// Debug.DrawLine( new Vector3(ax, 0.0f, ay), new Vector3(ax+az, 0.0f, ay+aw), Color.green );

	Vector3 temp;

	bdp = portal_0_dims_z * aw - portal_0_dims_w * az;
	if( bdp > 0.0f )
	{
		cx = ax - portal_0_dims_x;
		cy = ay - portal_0_dims_y;

		u = ( cx * portal_0_dims_w - cy * portal_0_dims_z ) / bdp;
		if( u >= 0.0f && u <= 1.0f )
		{
			u = ( cx * aw - cy * az ) / bdp;
			if( u >= 0.0f && u <= 1.0f )
			{
				// Went through portal 0
				// Make translation
				temp = new Vector3( ball_co[id].x, 0.0f, ball_co[id].y );
				temp = m4_portal_1.MultiplyPoint( temp );

				ball_co[id].x = temp.x;
				ball_co[id].y = temp.z;
				
				// Rotate velocity
				c = Mathf.Cos( portal_tunnel_r );
				s = Mathf.Sin( portal_tunnel_r );

				cx = ball_vl[id].x;
				cy = ball_vl[id].y;

				ball_vl[id].x = c * cx - s * cy;
				ball_vl[id].y = s * cx + c * cy;

				ball_co[id] += ball_vl[id] * FIXED_TIME_STEP;

				Debug.Log("hi 0");

				return;
			}
		}
	}

	// Portal 1
	bdp = portal_1_dims_z * aw - portal_1_dims_w * az;
	if( bdp > 0.0f ) 
	{
		cx = ax - portal_1_dims_x;
		cy = ay - portal_1_dims_y;

		u = ( cx * portal_1_dims_w - cy * portal_1_dims_z ) / bdp;
		if( u >= 0.0f && u <= 1.0f )
		{
			u = ( cx * aw - cy * az ) / bdp;
			if( u >= 0.0f && u <= 1.0f )
			{
				// Went through portal 1
				// Make translation
				temp = new Vector3( ball_co[id].x, 0.0f, ball_co[id].y );
				temp = m4_portal_0.MultiplyPoint( temp );

				ball_co[id].x = temp.x;
				ball_co[id].y = temp.z;
				
				// Rotate velocity
				c = Mathf.Cos( -portal_tunnel_r );
				s = Mathf.Sin( -portal_tunnel_r );

				cx = ball_vl[id].x;
				cy = ball_vl[id].y;

				ball_vl[id].x = c * cx - s * cy;
				ball_vl[id].y = s * cx + c * cy;

				ball_co[id] += ball_vl[id] * FIXED_TIME_STEP;

				Debug.Log("hi 1");

				return;
			}
		}
	}
}

#endif

// Updates table colour target to appropriate player colour
void UpdateTableColor( uint idsrc )
{
	if( !sn_open )
	{
		if( (idsrc ^ sn_playerxor) == 0 )
		{
			// Set table colour to blue
			tableSrcColour = pColour0;
		}
		else
		{
			// Table colour to orange
			tableSrcColour = pColour1;
		}

		cueRenderers[ sn_playerxor ].sharedMaterial.SetColor( uniform_cue_colour, pColour0 );
		cueRenderers[ sn_playerxor ^ 0x1U ].sharedMaterial.SetColor( uniform_cue_colour, pColour1 );
	}
	else
	{
		tableSrcColour = k_tableColorWhite;

		cueRenderers[ 0 ].sharedMaterial.SetColor( uniform_cue_colour, k_tableColorWhite );
		cueRenderers[ 1 ].sharedMaterial.SetColor( uniform_cue_colour, k_tableColorWhite );
	}

	CueGripMaterials[ sn_turnid ].SetColor( uniform_marker_colour, k_gripColourActive );
	CueGripMaterials[ sn_turnid ^ 0x1U ].SetColor( uniform_marker_colour, k_gripColourInactive );
}

// Called when a player first sinks a ball whilst the table was previously open
void DisplaySetLocal()
{
	uint picker = sn_turnid ^ sn_playerxor;

#if HT8B_DEBUGGER
	FRP( FRP_YES + "(local) " + Networking.GetOwner( playerTotems[sn_turnid] ).displayName + ":" + sn_turnid + " is " + 
		(picker == 0? "blues": "oranges") + FRP_END );
#endif

	UpdateTableColor( sn_turnid );
	UpdateScoreCardLocal();

	scoreCardRenderer.sharedMaterial.SetColor( uniform_scorecard_colour0, sn_playerxor == 0? pColour0: pColour1 );
	scoreCardRenderer.sharedMaterial.SetColor( uniform_scorecard_colour1, sn_playerxor == 1? pColour0: pColour1 );
}

// End of the game. Both with/loss
void GameOverLocal()
{
#if HT8B_DEBUGGER
	FRP( FRP_YES + "(local) Winner of match: " + Networking.GetOwner( playerTotems[sn_winnerid] ).displayName + FRP_END );
#endif

	UpdateTableColor( sn_winnerid );

	infText.text = Networking.GetOwner(playerTotems[sn_winnerid]).displayName + " wins!";
	infBaseTransform.SetActive( true );
	infHowToStart.SetActive( true );

	UpdateScoreCardLocal();
}

void OnTurnChangeLocal()
{
#if HT8B_DEBUGGER
	FRP( FRP_YES + "(local) turn switch to: " + Networking.GetOwner( playerTotems[sn_turnid] ).displayName + FRP_END );
#endif

	UpdateTableColor( sn_turnid );

	aud_main.PlayOneShot( snd_NewTurn, 1.0f );

	// Register correct cuetip
	cuetip = cueTips[ sn_turnid ];
}

void UpdateScoreCardLocal()
{
	int[] counter0 = new int[2];

	uint temp = sn_pocketed;

	for( int j = 0; j < 2; j ++ )
	{
		for( int i = 0; i < 7; i ++ )
		{
			if( (temp & 0x4) > 0 )
			{
				counter0[ j ^ sn_playerxor ] ++;
			}

			temp >>= 1;
		}
	}

	// Add black ball if we are winning the thing
	if( sn_gameover )
	{
		counter0[ sn_winnerid ] += (int)((sn_pocketed & 0x2) >> 1);
	}

	scoreCardRenderer.sharedMaterial.SetVector( uniform_scorecard_info, new Vector4( counter0[0]*0.0625f, counter0[1]*0.0625f, 0.0f, 0.0f ) );
}

// Player scored an objective ball
void OnPocketGood()
{
	// Make a bright flash
	tableCurrentColour *= 1.9f;

	aud_main.PlayOneShot( snd_Sink, 1.0f );
}

// Player scored a foul ball (cue, non-objective or 8 before set cleared)
void OnPocketBad()
{
	tableCurrentColour = pColourErr;

	aud_main.PlayOneShot( snd_Sink, 1.0f );
}

void ShowBalls( bool state )
{
	for( int i = 0; i < 16; i ++ )
	{
		balls_render[ i ].SetActive( state );
	}
}

void NewGameLocal()
{
	VRCPlayerApi startPlayer = Networking.GetOwner(playerTotems[0]);

#if HT8B_DEBUGGER
	FRP( FRP_YES + "(local) " + ( startPlayer != null? startPlayer.displayName: "[null]" ) + " started a new game" + FRP_END );
#endif

	// Put names on the board
	if( startPlayer != null )
	{
		playerNames[0].text = Networking.GetOwner(playerTotems[0]).displayName;
		playerNames[1].text = Networking.GetOwner(playerTotems[1]).displayName;
	}

	//tableSrcColour = tableColorWhite;
	UpdateTableColor( 0 );

	introAminTimer = 2.0f;
	aud_main.PlayOneShot( snd_Intro, 1.0f );

	// Turn off info
	infBaseTransform.SetActive( false );
	infHowToStart.SetActive( false );

	ShowBalls( true );

	UpdateScoreCardLocal();

	isReposition = false;
}

// REGION PHYSICS ENGINE
// =========================================================================================================================

// Cue input tracking

Vector3	cue_lpos;
Vector3	cue_llpos;
Vector3	cue_vdir;
Vector2	cue_shotdir;
float		cue_fdir;

// Timing

#if HT_QUEST
const float MAX_DELTA = 0.075f;						// Maximum steps/frame ( 5 ish )
#else
const float MAX_DELTA = 0.1f;						// Maximum steps/frame ( 8 )
#endif

// Calculation constants (measurements are in meters)

const float TABLE_WIDTH		= 1.0668f;					// horizontal span of table
const float TABLE_HEIGHT	= 0.6096f;					// vertical span of table
const float BALL_DIAMETRE	= 0.06f;						// width of ball
const float BALL_1OR			= 16.66666666666666f;	// 1 over ball radius
const float BALL_RSQR		= 0.0009f;					// ball radius squared
const float BALL_DSQR		= 0.0036f;					// ball diameter squared
const float BALL_DSQRPE		= 0.003481f;				// ball diameter squared plus epsilon
const float POCKET_RADIUS	= 0.09f;						// Full diameter of pockets (exc ball radi)

const float K_1OR2			= 0.70710678118f;			// 1 over root 2 (normalize +-1,+-1 vector)
const float K_1OR5			= 0.4472135955f;			// 1 over root 5 (normalize +-1,+-2 vector)

const float POCKET_DEPTH	= 0.04f;						// How far back (roughly) do pockets absorb balls after this point
const float MIN_VELOCITY	= 0.00005625f;				// SQUARED

const float FRICTION_EFF	= 0.99f;						// How much to multiply velocity by each update

#if HT_QUEST
uint ANDROID_UNIFORM_CLOCK = 0x00u;
uint ANDROID_CLOCK_DIVIDER = 0x8u;
#endif

#if HT_QUEST
#else
public Vector3	dkTargetPos;				// Target for desktop aiming
#endif

// Send ball to the gulag
void PocketBall( int id )
{
	uint total = 0U;

	// Get total for X positioning
	for( int i = 0; i < 16; i ++ )
	{
		total += (sn_pocketed >> i) & 0x1U;
	}

	// Put balls on the edge of the table for now
	// TODO: propper display
	ball_co[ id ].x = -TABLE_WIDTH + (float)total * BALL_DIAMETRE;
	ball_co[ id ].y = TABLE_HEIGHT + BALL_DIAMETRE * 2.0f;

	sn_pocketed ^= 1U << id;

	uint bmask = 0x1FCU << ((int)(sn_turnid ^ sn_playerxor) * 7);

	// Good pocket
	if( ((0x1U << id) & ((bmask) | (sn_open ? 0xFFFCU: 0x0000U) | ((bmask & sn_pocketed) == bmask? 0x2U: 0x0U))) > 0 )
	{
		OnPocketGood();
	}
	else
	{
		// bad
		OnPocketBad();
	}
}

// TODO: Inline
bool BallInPlay( int id )
{
	return ((sn_pocketed >> id) & 0x1U) == 0x00U;
}

// Check pocket condition
void BallPockets( int id )
{
	if( !BallInPlay( id ) )
		return;

	float zy, zx;
	Vector2 A;

	A = ball_co[ id ];

	// Setup major regions
	zx = A.x > 0.0f ? 1.0f: -1.0f;
	zy = A.y > 0.0f ? 1.0f: -1.0f;

	// Its in a pocket
	if( A.y*zy > TABLE_HEIGHT + POCKET_DEPTH || A.y*zy > A.x*-zx + TABLE_WIDTH+TABLE_HEIGHT + POCKET_DEPTH )
	{
		PocketBall( id );
	}
}

// Makes sure that velocity is not opposing surface normal
void ClampBallVelSemi( int id, Vector2 surface )
{
	// TODO: improve this method to be a bit more accurate
	if( Vector2.Dot( ball_vl[id], surface ) < 0.0f )
	{
		ball_vl[id] = ball_vl[id].magnitude * surface;
	}
}

// Is cue touching another ball?
bool CueContacting()
{
	for( int i = 1; i < 16; i ++ )
	{
		if( (ball_co[0] - ball_co[i]).sqrMagnitude < BALL_DSQR )
		{
			return true;
		}
	}

	return false;
}

// TODO: inline this
void BallEdges( int id )
{
	if( !BallInPlay( id ) )
		return;

	float zy, zx, zz, zw, d, k, i, j, l, r;
	Vector2 A, N;

	A = ball_co[ id ];

	// REGIONS
	/*  
		*  QUADS:							SUBSECTION:				SUBSECTION:
		*    zx, zy:							zz:						zw:
		*																
		*  o----o----o  +:  1			\_________/				\_________/
		*  | -+ | ++ |  -: -1		     |	    /		              /  /
		*  |----+----|					  -  |  +   |		      -     /   |
		*  | -- | +- |						  |	   |		          /  +  |
		*  o----o----o						  |      |             /       |
		* 
		*/

	// Setup major regions
	zx = A.x > 0.0f ? 1.0f: -1.0f;
	zy = A.y > 0.0f ? 1.0f: -1.0f;

	// within pocket regions
	if( (A.y*zy > (TABLE_HEIGHT-POCKET_RADIUS)) && (A.x*zx > (TABLE_WIDTH-POCKET_RADIUS) || A.x*zx < POCKET_RADIUS) )
	{
		// Subregions
		zw = A.y * zy > A.x * zx - TABLE_WIDTH + TABLE_HEIGHT ? 1.0f : -1.0f;

		// Normalization / line coefficients change depending on sub-region
		if( A.x * zx > TABLE_WIDTH * 0.5f )
		{
			zz = 1.0f;
			r = K_1OR2;
		}
		else
		{
			zz = -2.0f;
			r = K_1OR5;
		}

		// Collider line EQ
		d = zx * zy * zz; // Coefficient
		k = (-(TABLE_WIDTH * Mathf.Max(zz, 0.0f)) + POCKET_RADIUS * zw * Mathf.Abs( zz ) + TABLE_HEIGHT) * zy; // Konstant

		// Check if colliding
		l = zw * zy;
		if( A.y * l > (A.x * d + k) * l )
		{
			// Get line normal
			N = new Vector2(zx * zz, -zy) * zw * r;

			// New position
			i = (A.x * d + A.y - k) / (2.0f * d);
			j = i * d + k;

			ball_co[ id ] = new Vector2( i, j );

			// Reflect velocity
			ball_vl[ id ] = Vector2.Reflect( ball_vl[ id ], N );

			ClampBallVelSemi( id, N );
		}
	}
	else // edges
	{
		if( A.x * zx > TABLE_WIDTH )
		{
			ball_co[id].x = TABLE_WIDTH * zx;
			ball_vl[id] = Vector2.Reflect( ball_vl[id], Vector2.left * zx );

			ClampBallVelSemi( id, Vector2.left * zx );
		}

		if( A.y * zy > TABLE_HEIGHT )
		{
			ball_co[id].y = TABLE_HEIGHT * zy;
			ball_vl[id] = Vector2.Reflect( ball_vl[id], Vector2.down * zy );

			ClampBallVelSemi( id, Vector2.down * zy );
		}
	}
}

// Advance simulation 1 step for ball id
void BallSimulate( int id )
{
	// Apply friction
	ball_vl[ id ] *= FRICTION_EFF;

	Vector2 mov_delta = ball_vl[id] * FIXED_TIME_STEP;
	float mov_mag = mov_delta.magnitude;

	// Rotate visual object by pure rolling
	if( id > 0 ) 
		balls_render[ id ].transform.Rotate( new Vector3( mov_delta.y, 0.0f, -mov_delta.x ) / mov_mag, mov_mag * BALL_1OR * Mathf.Rad2Deg, Space.World );

	// ball/ball collisions
	for( int i = id+1; i < 16; i++ )
	{
		if( !BallInPlay( i ) )
			continue;

		Vector2 delta = ball_co[ i ] - ball_co[ id ];
		float dist = delta.magnitude;

		if( dist < BALL_DIAMETRE )
		{
			Vector2 normal = delta / dist;

			Vector2 velocityDelta = ball_vl[ id ] - ball_vl[ i ];

			float dot = Vector2.Dot( velocityDelta, normal );

			if( dot > 0.0f ) 
			{
				Vector2 reflection = normal * dot;
				ball_vl[id] -= reflection;
				ball_vl[i] += reflection;

				//aud_click.volume = Mathf.Clamp( ball_velocities[id].sqrMagnitude * 0.2f, 0.0f, 1.0f ); 
					
				// Prevent sound spam if it happens
				if( ball_vl[id].sqrMagnitude > 0 )
					aud_main.PlayOneShot( snd_Hits[ 0 ], 1.0f );

				// First hit detected
				if( id == 0 && sn_firsthit == 0 )
				{
					sn_firsthit = i;
				}
			}
		}
	}

	// ball still moving about
	if( ball_vl[ id ].sqrMagnitude > MIN_VELOCITY )
	{
		ballsMoving = true;
	}
	else
	{
		// Put velocity to 0
		ball_vl[ id ] = Vector2.zero;

		return;
	}

	// BallPortal( id );
}

// ( Since v0.2.0a ) Check if we can predict a collision before move update happens to improve accuracy
bool Cue_PredictiveCollide()
{
	// Get what will be the next position
	Vector2 originalDelta = ball_vl[0]*FIXED_TIME_STEP;
	Vector2 norm = ball_vl[0].normalized;
	
	Vector2 h;
	float lf, s, nmag;

	// Closest found values
	float minlf = 9999999.0f;
	int minid = 0;
	float mins = 0;

	// Loop balls look for collisions
	for( int i = 1; i < 16; i ++ )
	{
		if( !BallInPlay( i ) )
			continue;

		h = ball_co[ i ] - ball_co[ 0 ];
		lf = Vector2.Dot( norm, h );
		s = BALL_DSQRPE - Vector2.Dot( h, h ) + lf * lf;

		if( s < 0.0f )
			continue;

		if( lf < minlf )
		{
			minlf = lf;
			minid = i;
			mins = s;
		}
	}

	if( minid > 0 )
	{
		nmag = minlf-Mathf.Sqrt( mins );

		// Assign new position if got appropriate magnitude
		if( nmag * nmag < originalDelta.sqrMagnitude )
		{
			ball_co[ 0 ] += norm * nmag;
			return true;
		}
	}

	return false;
}

// Ray circle intersection
// yes, its fixed size circle
// Output is dispensed into the below variable
// One intersection point only
// This is not used in physics calcuations, only cue input

Vector2 RayCircle_output;
bool RayCircle( Vector2 start, Vector2 dir, Vector2 circle )
{
	Vector2 nrm = dir.normalized;
	Vector2 h = circle - start;
	float lf = Vector2.Dot( nrm, h );
	float s = BALL_RSQR - Vector2.Dot( h, h ) + lf * lf;

	if( s < 0.0f ) return false;

	s = Mathf.Sqrt( s );

	if( lf < s )
	{
		if( lf + s >= 0 )
		{
			s = -s;
		}
		else
		{
			return false;
		}
	}

	RayCircle_output = start + nrm * (lf-s);
	return true;
}

Vector3 RaySphere_output;
bool RaySphere( Vector3 start, Vector3 dir, Vector3 sphere )
{
	Vector3 nrm = dir.normalized;
	Vector3 h = sphere - start;
	float lf = Vector3.Dot( nrm, h );
	float s = BALL_RSQR - Vector3.Dot(h, h) + lf * lf;

	if( s < 0.0f ) return false;

	s = Mathf.Sqrt( s );

	if( lf < s )
	{
		if( lf + s >= 0 )
		{
			s = -s;
		}
		else
		{
			return false;
		}
	}

	RaySphere_output = start + nrm * (lf-s);
	return true;
}

// Closest point on line from pos
Vector2 LineProject( Vector2 start, Vector2 dir, Vector2 pos )
{
	return start + dir * Vector2.Dot( pos - start, dir );
}

// Setup player's turn
void Owner_NewTurn()
{
#if MULTIGAMES_PORTAL
	PortalRandomize();
#endif

#if HT8B_DEBUGGER
	FRP( FRP_YES + "NewTurn()" + FRP_END );
#endif

#if !HT_QUEST
	dk_updatetarget();
#endif

	// Fixup game state
	if( sn_foul )
	{
		#if HT8B_DEBUGGER
		FRP( FRP_LOW + "Game state fixup" + FRP_END );
		#endif

		// Allow repositioning anywhere
		isReposition = true;
		repoMaxX = TABLE_WIDTH;
		markerObj.SetActive( true );

		// Cue ball is out of play
		if( (sn_pocketed & 0x1U) != 0 )
		{
			ball_co[0] = ball_og[0];
			ball_vl[0] = Vector2.zero;
			
			markerObj.transform.localPosition = Vector3.zero;

			// Save out position
			// NetPack( sn_turnid );

			// https://vrchat.canny.io/vrchat-udon-closed-alpha-feedback/p/bitwisenot-for-integer-built-in-types
			// sn_pocketed &= ~0x1U;

			sn_pocketed &= 0xFFFFFFFEU;
		}
		else
		{
			markerObj.transform.localPosition = new Vector3( ball_co[0].x, 0.0f, ball_co[0].y );
		}
	}

	sn_permit = true;
	sn_foul = false;
	sn_firsthit = 0;

	// Propogate any updates we made
	NetPack( sn_turnid );
}

void SimEnd_Win( uint winner )
{
	#if HT8B_DEBUGGER
	FRP( FRP_LOW + " -> GAMEOVER" + FRP_END );
	#endif

	sn_gameover = true;
	sn_winnerid = winner;

	GameOverLocal();

	NetPack( sn_turnid );
	NetRead();
}

void SimEnd_Pass()
{
	#if HT8B_DEBUGGER
	FRP( FRP_LOW + " -> PASS" + FRP_END );
	#endif

	NetPack( sn_turnid ^ 0x1U );
	NetRead();
}

void SimEnd_Foul()
{
	#if HT8B_DEBUGGER
	FRP( FRP_LOW + " -> FOUL" + FRP_END );
	#endif

	sn_foul = true;

	SimEnd_Pass();
}

void SimEnd_Continue()
{
	#if HT8B_DEBUGGER
	FRP( FRP_LOW + " -> COTNINUE" + FRP_END );
	#endif

	// Close table if it was open
	if( sn_open )
	{
		sn_open = false;

		// Player triggered turn xor
		// check which group has the most sinks and 
		if((sn_pocketed & 0x1FC) > ((sn_pocketed & 0xFE00) >> 7))
		{
			sn_playerxor = sn_turnid;
		}
		else
		{
			sn_playerxor = sn_turnid ^ 0x1u;
		}

		DisplaySetLocal();
	}

	Owner_NewTurn();
	// NetPack( sn_turnid ); <- this is called in Owner_NewTurn();
	NetRead();
}

// once balls stops rolling this is called
void SimEnd()
{
	sn_simulating = false;

	#if HT8B_DEBUGGER
	FRP( FRP_LOW + "(local) SimEnd()" + FRP_END );
	#endif

	// TODO: split state checking into more manageable chunks
	if( Networking.GetOwner( this.gameObject ) == Networking.LocalPlayer )
	{
		// Owner state checks
		#if HT8B_DEBUGGER
		FRP( FRP_LOW + "Post-move state checking" + FRP_END );
		#endif

		uint bmask = 0xFFFCU;
		uint emask = 0x0U;

		// Quash down the mask if table has closed
		if( !sn_open )
		{
			bmask = bmask & (0x1FCU << ((int)(sn_playerxor ^ sn_turnid) * 7));
			emask = 0x1FCU << ((int)(sn_playerxor ^ sn_turnid ^ 0x1U) * 7);
		}

		// Common informations
		bool isSetComplete = (sn_pocketed & bmask) == bmask;
		bool isScratch = (sn_pocketed & 0x1U) == 0x1U;
		bool is8Sink = (sn_pocketed & 0x2U) == 0x2U;

		// Append black to mask if set is done
		if( isSetComplete )
		{
			bmask |= 0x2U;
		}

		bool isObjectiveSink = (sn_pocketed & bmask) > (sn_pocketed_prv & bmask);
		bool isOpponentSink = (sn_pocketed & emask) > (sn_pocketed_prv & emask);

		// Calculate if objective was not hit first
		bool isWrongHit = ((0x1U << sn_firsthit) & bmask) == 0;

		bool winCondition = isSetComplete && is8Sink;
		bool foulCondition = isScratch || isWrongHit;

		if( winCondition )
		{
			if( foulCondition )
			{
				// Loss
				SimEnd_Win( sn_turnid ^ 0x1U );
			}
			else
			{
				// Win
				SimEnd_Win( sn_turnid );
			}
		}
		else if( is8Sink )
		{
			// Loss
			SimEnd_Win( sn_turnid ^ 0x1U );
		}
		else if( foulCondition )
		{
			// Foul
			SimEnd_Foul();
		}
		else if( isObjectiveSink && !isOpponentSink )
		{
			// Continue
			SimEnd_Continue();
		}
		else
		{
			// Pass
			SimEnd_Pass();
		}
	}
	// Check if there was a network update on hold
	if( sn_updatelock )
	{
		#if HT8B_DEBUGGER
		FRP( FRP_LOW + "Update was waiting, executing now" + FRP_END );
		#endif

		sn_updatelock = false;

		NetRead();
	}
}

// Run one physics iteration for all balls
void PhysicsUpdate()
{
	ballsMoving = false;

	// Cue angular velocity
	if( (sn_pocketed & 0x1) == 0 )
	{
		cue_avl *= 0.96f;
		ball_vl[0] += cue_avl * FIXED_TIME_STEP;

		if( !Cue_PredictiveCollide() )
		{
			// Apply movement
			ball_co[ 0 ] += ball_vl[ 0 ] * FIXED_TIME_STEP;
		}

		BallSimulate( 0 );
	}

	// Run main simulation / inter-ball collision
	for( int i = 1; i < 16; i ++ )
	{
		if( BallInPlay( i ) )
		{
			ball_co[ i ] += ball_vl[ i ] * FIXED_TIME_STEP;
			
			BallSimulate( i );
		}
	}

	// Check if simulation has settled
	if( !ballsMoving )
	{
		if( sn_simulating )
		{
			SimEnd();
		}

		return;
	}

	// Run edge collision
	for( int i = 0; i < 16; i ++ )
	{
		BallEdges( i );
	}

	// Run triggers
	for( int i = 0; i < 16; i ++ )
	{
		BallPockets( i );
	}
}

// Events
public void StartHit()
{
	// lock aim variables
	sn_armed = true;
}

public void EndHit()
{
	sn_armed = false;
}

#if !HT_QUEST
void dk_updatetarget()
{
	// Update desktop targets
	dkTargetPos = this.transform.TransformPoint( new Vector3( ball_co[ 0 ].x, 0.0f, ball_co[ 0 ].y ) );
	
	gripControllers[ sn_turnid ].dk_cpytarget();
}
#endif

public void PosFinalize()
{
	if( !CueContacting() )
	{
		isReposition = false;
		markerObj.SetActive( false );

#if !HT_QUEST
		dk_updatetarget();
#endif

		// Save out position to remote clients
		NetPack( sn_turnid );
	}
}

float timeLast;
float accum;

private void Update()
{
	// Physics step accumulator routine
	float time = Time.timeSinceLevelLoad;
	float timeDelta = time - timeLast;

	if ( timeDelta > MAX_DELTA )
	{
		timeDelta = MAX_DELTA;
	}

	timeLast = time;
		
	// Run sim only if things are moving
	if( sn_simulating )
	{
		accum += timeDelta;

		while ( accum >= FIXED_TIME_STEP )
		{
			PhysicsUpdate();
			accum -= FIXED_TIME_STEP;
		}
	}

	// Update rendering objects positions
	for( int i = 0; i < 16; i ++ )
	{
		balls_render[i].transform.localPosition = new Vector3( ball_co[i].x, 0.0f, ball_co[i].y );
	}

	cue_lpos = this.transform.InverseTransformPoint( cuetip.transform.position );
	Vector3 lpos2 = cue_lpos;

	// cue ball in 'world space' ( actually, is local space )
	Vector3 ball0ws = new Vector3( ball_co[0].x, 0.0f, ball_co[0].y );
	
	// if shot is prepared for next hit
	if( sn_permit )
	{
		bool isContact = false;

		if( isReposition )
		{
			// Clamp position to table / kitchen
			Vector3 temp = markerObj.transform.localPosition;
			temp.x = Mathf.Clamp( temp.x, -TABLE_WIDTH, repoMaxX );
			temp.z = Mathf.Clamp( temp.z, -TABLE_HEIGHT, TABLE_HEIGHT );
			temp.y = 0.0f;
			markerObj.transform.localPosition = temp;
			markerObj.transform.localRotation = Quaternion.identity;

			ball_co[0] = new Vector2( temp.x, temp.z );
			balls_render[0].transform.localPosition = temp;

			isContact = CueContacting();

			if( isContact )
			{
				markerRender.sharedMaterial.SetColor( uniform_marker_colour, markerColorNO );
			}
			else
			{
				markerRender.sharedMaterial.SetColor( uniform_marker_colour, markerColorOK );
			}
		}

		if( sn_armed && !isContact )
		{
			float sweep_time_ball = Vector3.Dot( ball0ws - cue_llpos, cue_vdir );

			// Check for potential skips due to low frame rate
			if( sweep_time_ball > 0.0f && sweep_time_ball < (cue_llpos - lpos2).magnitude )
			{
				lpos2 = cue_llpos + cue_vdir * sweep_time_ball;
			}

			// Hit condition is when cuetip is gone inside ball
			if( (lpos2 - ball0ws).sqrMagnitude < BALL_RSQR )
			{

#if HT8B_ALLOW_AUTOSWITCH
				// This check is here for stability when using auto-transfer
				if( Networking.GetOwner( playerTotems[ sn_turnid ] ) == Networking.LocalPlayer )
#else
				if( Networking.GetOwner( this.gameObject ) == Networking.LocalPlayer )
#endif
				{
					// Make sure repositioner is turned off if the player decides he just
					// wanted to hit it without putting it somewhere
					isReposition = false;
					markerObj.SetActive( false );

					devhit.SetActive( false );
					guideline.SetActive( false );

					// Compute velocity delta
					float vel = (lpos2 - cue_llpos).magnitude * 10.0f;

					// weeeeeeee
					ball_vl[0] = cue_shotdir * Mathf.Min( vel, 1.0f ) * 14.0f;

					// ball avl is a function of velocity
					cue_avl = ball_vl[0] * RaySphere_output.y * 33.3333333333f;

					// Remove locks
					sn_armed = false;
					sn_permit = false;

					#if HT8B_DEBUGGER
					FRP( FRP_LOW + "Commiting changes" + FRP_END );
					#endif

					// Commit changes
					sn_simulating = true;
					sn_pocketed_prv = sn_pocketed;

#if !HT_QUEST
					// Remove desktop locks
					gripControllers[0].dk_endhit();
					gripControllers[1].dk_endhit();
#endif

					NetPack( sn_turnid );
					NetRead();
				}
			}
		}
		else
		{
			cue_vdir = this.transform.InverseTransformVector( cuetip.transform.forward );//new Vector2( cuetip.transform.forward.z, -cuetip.transform.forward.x ).normalized;

			// Get where the cue will strike the ball
			if( RaySphere( lpos2, cue_vdir, ball0ws ))
			{
				guideline.SetActive( true );
				devhit.SetActive( true );
				devhit.transform.localPosition = RaySphere_output;
				guidefspin.transform.localScale = new Vector3( RaySphere_output.y * 33.3333333333f, 1.0f, 1.0f );

				Vector3 scuffdir = ( ball0ws - RaySphere_output ).normalized * 0.2f;
				cue_shotdir = new Vector2( cue_vdir.x, cue_vdir.z );
				cue_shotdir += new Vector2( scuffdir.x, scuffdir.z );
				cue_shotdir = cue_shotdir.normalized;

				// TODO: add scuff offset to vdir
				cue_fdir = Mathf.Atan2( cue_shotdir.y, cue_shotdir.x );

				// Update the prediction line direction
				guideline.transform.localEulerAngles = new Vector3( 0.0f, -cue_fdir * Mathf.Rad2Deg, 0.0f );
			}
			else
			{
				devhit.SetActive( false );
				guideline.SetActive( false );
			}
		}
	}

	cue_llpos = lpos2;

	// Table outline colour
	if( sn_gameover )
	{
		// Flashing if we won
		#if !HT_QUEST
		tableCurrentColour = tableSrcColour * (Mathf.Sin( Time.timeSinceLevelLoad * 3.0f) * 0.5f + 1.0f);
		#endif
		
		infBaseTransform.transform.localPosition = new Vector3( 0.0f, Mathf.Sin( Time.timeSinceLevelLoad ) * 0.1f, 0.0f );
		infBaseTransform.transform.Rotate( Vector3.up, 90.0f * Time.deltaTime );
	}
	else
	{
		#if !HT_QUEST
		tableCurrentColour = Color.Lerp( tableCurrentColour, tableSrcColour, Time.deltaTime * 3.0f );
		#else

		// Run uniform updates at a slower rate on android (/8)
		ANDROID_UNIFORM_CLOCK ++;

		if( ANDROID_UNIFORM_CLOCK >= ANDROID_CLOCK_DIVIDER )
		{
			tableCurrentColour = Color.Lerp( tableCurrentColour, tableSrcColour, Time.deltaTime * 24.0f );
			tableRenderer.sharedMaterial.SetColor( uniform_tablecolour, tableCurrentColour );

			ANDROID_UNIFORM_CLOCK = 0x00u;
		}

		#endif
	}

	#if !HT_QUEST
	tableRenderer.sharedMaterial.SetColor( uniform_tablecolour, tableCurrentColour );
	#endif

	// Intro animation
	if( introAminTimer > 0.0f )
	{
		introAminTimer -= Time.deltaTime;

		Vector3 temp;
		float atime;
		float aitime;

		if( introAminTimer < 0.0f )
			introAminTimer = 0.0f;

		// Cueball drops late
		temp = balls_render[0].transform.localPosition;
		atime = Mathf.Clamp(introAminTimer - 0.33f, 0.0f, 1.0f); 
		aitime = (1.0f - atime);
		temp.y = Mathf.Abs(Mathf.Cos(atime * 6.29f)) * atime * 0.5f;
		balls_render[0].transform.localPosition = temp;
		balls_render[0].transform.localScale = new Vector3(aitime, aitime, aitime);

		for ( int i = 1; i < 16; i ++ )
		{
			temp = balls_render[i].transform.localPosition;
			atime = Mathf.Clamp(introAminTimer - 0.84f - (float)i * 0.03f, 0.0f, 1.0f);
			aitime = (1.0f - atime);

			temp.y = Mathf.Abs( Mathf.Cos( atime * 6.29f ) ) * atime * 0.5f;
			balls_render[i].transform.localPosition = temp;
			balls_render[i].transform.localScale = new Vector3(aitime, aitime, aitime);
		}
	}

	#if MULTIGAMES_PORTAL
	if( FORCERANDOM )
	{
		PortalRandomize();
		FORCERANDOM = false;
	}
	#endif
}

// Copy current values to previous values, no memcpy here >:(
void sn_copyprv()
{
	// Init _prv states
	sn_pocketed_prv = sn_pocketed;
	sn_simulating_prv = sn_simulating;
	sn_turnid_prv = sn_turnid;
	sn_foul_prv = sn_foul;
	sn_open_prv = sn_open;
	sn_playerxor_prv = sn_playerxor;
	sn_gameover_prv = sn_gameover;
	sn_winnerid_prv = sn_winnerid;
	sn_permit_prv = sn_permit;
	sn_rs_call8_prv = sn_rs_call8;
	sn_rs_call_prv = sn_rs_call;
	sn_rs_anyf_prv = sn_rs_anyf;
	sn_gameid_prv = sn_gameid;
	sn_colourid_prv = sn_colourid;
}

private void Start()
{
	sn_copyprv();

	#if HT8B_DEBUGGER
	FRP( FRP_LOW + "Starting" + FRP_END );
	#endif

#if USE_INT_UNIFORMS

	// Gather shader uniforms
	uniform_tablecolour = Shader.PropertyToID( "_EmissionColour" );
	uniform_scorecard_colour0 = Shader.PropertyToID( "_Colour0" );
	uniform_scorecard_colour1 = Shader.PropertyToID( "_Colour1" );
	uniform_scorecard_info = Shader.PropertyToID( "_Info" );
	uniform_marker_colour = Shader.PropertyToID( "_Color" );
	uniform_cue_colour = Shader.PropertyToID( "_ReColor" );
	
#endif
	UpdateColourSources();
	UpdateTableColor( 0 );

	aud_main = this.GetComponent<AudioSource>();
	//tableRenderer = gametable.GetComponent<Renderer>();

	guidelineMat.SetMatrix( "_BaseTransform", this.transform.worldToLocalMatrix );

	// turn off guideline
	guideline.SetActive( false );
	devhit.SetActive( false );
	infBaseTransform.SetActive( false );
	markerObj.SetActive( false );

	for( int i = 0; i < 16; i ++ ) 
	{
		ball_og[i].x = balls_render[i].transform.localPosition.x;
		ball_og[i].y = balls_render[i].transform.localPosition.z;
		balls_render[i].SetActive(false);
	}

	//SetupBreak();

	NetPack( 0 );
	NetRead();
}

// Resets local game state to defined state
// TODO: Merge this with NewGame()
public void SetupBreak()
{
	#if HT8B_DEBUGGER
	FRP( FRP_LOW + "SetupBreak()" + FRP_END );
	#endif

	sn_pocketed = 0x00;
	sn_pocketed_prv = 0x00;
	sn_simulating = false;
	sn_open = true;
	sn_gameover = false;

	// Doesnt need to be set but for consistencys sake
	sn_playerxor = 0;
	sn_winnerid = 0;

	for( int i = 0; i < 16; i ++ )
	{
		ball_co[ i ] = ball_og[ i ];
		ball_vl[ i ] = Vector2.zero;
	}

	NewGameLocal();
}

public void SendDebugImpulse()
{
	#if HT8B_DEBUGGER
	FRP( "Resetting" );
	#endif

	SetupBreak();

	// Re-encode positions
	NetPack( 0 );
	NetRead();
}

// ** experimental ** yoink turn from other player
// TODO: maybe review transfer system to instead only use
// cue IDs.

public void AutoTake0()
{
	if( sn_turnid == 0 && sn_permit )
	{
		Networking.SetOwner( Networking.LocalPlayer, this.gameObject );
	}
}

public void AutoTake1()
{
	if( sn_turnid == 1 && sn_permit )
	{
		Networking.SetOwner( Networking.LocalPlayer, this.gameObject );
	}
}

public void NewGame()
{
	// This will get called by all clients who observe the collision
	// between the two sticks. Therefore extra checks are done to make
	// sure this only runs predictably

	#if HT8B_DEBUGGER
	FRP( FRP_LOW + "(local) NewGame()" + FRP_END );
	#endif

	if( Networking.GetOwner( playerTotems[0] ) == Networking.LocalPlayer )
	{
		// Check if game in progress
		if( sn_gameover )
		{
			#if HT8B_DEBUGGER
			FRP( FRP_YES + "Starting new game" + FRP_END );
			#endif

			Networking.SetOwner( Networking.LocalPlayer, this.gameObject );

			sn_gameid ++;

			SetupBreak();

			// Override allow repositioning within kitchen
			isReposition = true;
			repoMaxX = -TABLE_WIDTH * 0.5f;
			markerObj.transform.localPosition = new Vector3( ball_og[0].x, 0.0f, ball_og[0].y );
			markerObj.SetActive( true );

			Owner_NewTurn();

			// TODO: send which totem ID started the game instead
			NetPack( 0 );
			NetRead();
		}
		else
		{
			#if HT8B_DEBUGGER
			FRP( FRP_WARN + "game in progress" + FRP_END );
			#endif
		}
	}
	else
	{
		// FRP( FRP_WARN + "(local) not player 0" + FRP_END );
	}
}

// reset game
public void ForceEndGame()
{
	// Limit reset to totem owners ownly
	if( Networking.LocalPlayer == Networking.GetOwner( playerTotems[0] ) ||
		Networking.LocalPlayer == Networking.GetOwner( playerTotems[1] ))
	{
		#if HT8B_DEBUGGER
		FRP( FRP_WARN + "Ending game early" + FRP_END );
		#endif

		Networking.SetOwner( Networking.LocalPlayer, this.gameObject );

		sn_gameover = true;
		sn_simulating = false;
		sn_permit = false;

		// sn_winnerid		= 0x00U;

		// For good measure in case other clients trigger an event whilst owner
		sn_packetid += 2;

		GameOverLocal();

		NetPack( sn_turnid );
	}
	else
	{
		#if HT8B_DEBUGGER
		FRP( FRP_ERR + "Reset is availible to: " + Networking.GetOwner( playerTotems[0] ).displayName + " and " + Networking.GetOwner( playerTotems[1] ).displayName + FRP_END );
		#endif
	}
}

// REGION NETWORKING
// =========================================================================================================================

const float I16_MAXf = 32767.0f;

void EncodeUint16( int pos, ushort v ) 
{
	net_data[ pos ] = (byte)(v & 0xff);
	net_data[ pos + 1 ] = (byte)(((uint)v >> 8) & 0xff);
}

ushort DecodeUint16( int pos ) 
{
	return (ushort)(net_data[pos] | (((uint)net_data[pos+1]) << 8));
}

// 4 char string from Vector2. Encodes floats in: [ -range, range ] to 0-65535
void Encodev2( int pos, Vector2 vec, float range )
{
	EncodeUint16( pos, (ushort)((vec.x / range) * I16_MAXf + I16_MAXf ) );
	EncodeUint16( pos + 2, (ushort)((vec.y / range) * I16_MAXf + I16_MAXf ) );
}

// Decode 4 chars at index to Vector2. Decodes from 0-65535 to [ -range, range ]
Vector2 Decodev2( int start, float range )
{
	ushort _x = DecodeUint16( start );
	ushort _y = DecodeUint16( start + 2 );

	float x = ((_x - I16_MAXf) / I16_MAXf) * range;
	float y = ((_y - I16_MAXf) / I16_MAXf) * range;
		
	return new Vector2( x, y );
} 

// Encode all data of game state into netstr
public void NetPack( uint _turnid )
{
	sn_packetid ++;

	// Garuntee array size by reallocating.. because c#
	net_data = new byte[0x52];

	// positions
	for ( int i = 0; i < 16; i ++ )
	{
		Encodev2( i * 4, ball_co[ i ], 2.5f );
	}

	// Cue ball velocity last
	Encodev2( 0x40, ball_vl[0], 50.0f );
	Encodev2( 0x44, cue_avl, 50.0f );

	// Encode pocketed imformation
	EncodeUint16( 0x48, (ushort)(sn_pocketed & 0x0000FFFFU) );

	// Game state
	uint flags = 0x0U;
	if( sn_simulating ) flags |= 0x1U;
	flags |= _turnid << 1;
	if( sn_foul ) flags |= 0x4U;
	if( sn_open ) flags |= 0x8U;
	flags |= sn_playerxor << 4;
	if( sn_gameover ) flags |= 0x20U;
	flags |= sn_winnerid << 6;
	if( sn_permit ) flags |= 0x80U;

	EncodeUint16( 0x4A, (ushort)flags );
	EncodeUint16( 0x4C, sn_packetid );
	EncodeUint16( 0x4E, sn_gameid );
	EncodeUint16( 0x50, sn_colourid );

	netstr = Convert.ToBase64String( net_data, Base64FormattingOptions.None );

	#if HT8B_DEBUGGER
	FRP( FRP_LOW + "NetPack()" + FRP_END );
	#endif
}

// Decode networking string
// TODO: Clean up this function
public void NetRead()
{
	// CHECK ERROR ===================================================================================================
	#if HT8B_DEBUGGER
	FRP( FRP_LOW + "incoming base64: " + netstr + FRP_END );
	#endif

	byte[] in_data = Convert.FromBase64String( netstr );
	if( in_data.Length < 0x52 ) {
			
		#if HT8B_DEBUGGER
		FRP( FRP_WARN + "Sync string too short for decode, skipping\n" + FRP_END );
		#endif

		return; 
	}

	net_data = in_data;

	#if HT8B_DEBUGGER
	FRP( FRP_LOW + netstr_hex() + FRP_END );
	#endif

	// Throw out updates that are possible errournous
	ushort nextid = DecodeUint16( 0x4C );
	if( nextid < sn_packetid )
	{
		#if HT8B_DEBUGGER
		FRP( FRP_WARN + "Packet ID was old ( " + nextid + " < " + sn_packetid + " ). Throwing out update" + FRP_END );
		#endif

		return;
	}
	sn_packetid = nextid;

	// MAIN DECODE ===================================================================================================
	sn_copyprv();

	// Pocketed information
	// Ball positions, reset velocity
	for( int i = 0; i < 16; i ++ )
	{
		ball_vl[i] = Vector2.zero;
		ball_co[i] = Decodev2( i * 4, 2.5f );
	}

	ball_vl[0] = Decodev2( 0x40, 50.0f );
	cue_avl = Decodev2( 0x44, 50.0f );

	sn_pocketed = DecodeUint16( 0x48 );

	uint gamestate = DecodeUint16( 0x4A );
	sn_simulating = (gamestate & 0x1U) == 0x1U;
	sn_turnid = (gamestate & 0x2U) >> 1;
	sn_foul = (gamestate & 0x4U) == 0x4U;
	sn_open = (gamestate & 0x8U) == 0x8U;
	sn_playerxor = (gamestate & 0x10U) >> 4;
	sn_gameover = (gamestate & 0x20U) == 0x20U;
	sn_winnerid = (gamestate & 0x40U) >> 6;
	sn_permit = (gamestate & 0x80U) == 0x80U;

	sn_gameid = DecodeUint16( 0x4E );
	sn_colourid = DecodeUint16( 0x50 );

	// Events ==========================================================================================================

	if( !sn_permit )
	{
		// EV: 0

		markerObj.SetActive( false );
		devhit.SetActive( false );
		guideline.SetActive( false );
	}

	if( sn_gameid > sn_gameid_prv )
	{
		// EV: 1

		NewGameLocal();
	}

	// Check if turn was transferred
	if( sn_turnid != sn_turnid_prv )
	{
		// EV: 2

		#if HT8B_DEBUGGER
		FRP( FRP_LOW + "Ownership changed" + FRP_END );
		#endif

		// Fullfil ownership transfer early
		// Technically this is not needed with auto-switch mechanism, however its currently
		// not implemented anywhere else when a turn switch is made and both players are
		// already holding the respective cues, its not gonna let a player play cause
		// he doesnt have ownership of the script object
		//
		// TODO: Polish the auto-yoink system.

		if( Networking.GetOwner( playerTotems[ sn_turnid ] ) == Networking.LocalPlayer )
		{
			#if HT8B_DEBUGGER
			FRP( FRP_YES + "Transfered to local" + FRP_END );
			#endif

			if( sn_simulating )
			{
				// In THEORY this should never ever be hit, but there might be an edge case
				#if HT8B_DEBUGGER
				FRP( FRP_ERR + "Remote simulating when ownership transfer attempt was made... script is deadlocked! contact harry!" + FRP_END );
				#endif
			}
			else
			{
				// Give our local player permission to play his turn
				Networking.SetOwner( Networking.LocalPlayer, this.gameObject );
					
				// Sort out gamestate
				Owner_NewTurn();
					
				// Not sure why these were called ?
				// NetPack( sn_turnid );
				// NetRead();
			}
		}
		else
		{
			#if HT8B_DEBUGGER
			FRP( FRP_LOW + "Transfered to remote" + FRP_END );
			#endif
		}

		OnTurnChangeLocal();
	}

	// Table switches to closed
	if( sn_open_prv && !sn_open )
	{
		// EV: 3

		DisplaySetLocal();
	}

	// Check if game is over
	if(!sn_gameover_prv && sn_gameover)
	{
		// EV: 4

		GameOverLocal();
	}

	// Coloursets
	if(sn_colourid_prv != sn_colourid)
	{
		UpdateColourSources();
	}

	UpdateScoreCardLocal();
}

string netstr_hex()
{
	string str = "";

	for( int i = 0; i < net_data.Length; i += 2 )
	{
		ushort v = DecodeUint16( i );
		str += v.ToString("X4");
	}

	return str;
}

// Wait for updates to the synced netstr
public override void OnDeserialization()
{
	if( !string.Equals( netstr, netstr_prv ) )
	{
		#if HT8B_DEBUGGER
		FRP( FRP_LOW + "OnDeserialization() :: netstr update" + FRP_END );
		#endif

		netstr_prv = netstr;

		// Check if local simulation is in progress, the event will fire off later when physics
		// are settled by the client
		if( sn_simulating )
		{
			#if HT8B_DEBUGGER
			FRP( FRP_WARN + "local simulation is still running, the network update will occur after completion" + FRP_END );
			#endif

			sn_updatelock = true;
		}
		else
		{
			// We are free to read this update
			NetRead();
		}
	}
}

#if !HT_QUEST

const int FRP_MAX = 32;
int FRP_LEN = 0;
int FRP_PTR = 0;
string[] FRP_LINES = new string[32];

// Print a line to the debugger
void FRP( string ln )
{
	Debug.Log( "[<color=\"#B5438F\">ht8b</color>] " + ln );

	FRP_LINES[ FRP_PTR ++ ] = "[<color=\"#B5438F\">ht8b</color>] " + ln + "\n";
	FRP_LEN ++ ;

	if( FRP_PTR >= FRP_MAX )
	{
		FRP_PTR = 0;
	}

	if( FRP_LEN > FRP_MAX )
	{
		FRP_LEN = FRP_MAX;
	}

	string output = "ht8b 0.3.0a ";
		
	// Add information about game state:
	output += Networking.IsOwner(Networking.LocalPlayer, this.gameObject) ? 
		"<color=\"#95a2b8\">net(</color> <color=\"#4287F5\">OWNER</color> <color=\"#95a2b8\">)</color> ":
		"<color=\"#95a2b8\">net(</color> <color=\"#678AC2\">RECVR</color> <color=\"#95a2b8\">)</color> ";

	output += sn_simulating ?
		"<color=\"#95a2b8\">sim(</color> <color=\"#4287F5\">ACTIVE</color> <color=\"#95a2b8\">)</color> ":
		"<color=\"#95a2b8\">sim(</color> <color=\"#678AC2\">PAUSED</color> <color=\"#95a2b8\">)</color> ";

	VRCPlayerApi currentOwner = Networking.GetOwner(playerTotems[sn_turnid]);
	output += "<color=\"#95a2b8\">player(</color> <color=\"#4287F5\">"+ (currentOwner != null? currentOwner.displayName: "[null]") + ":" + sn_turnid + "</color> <color=\"#95a2b8\">)</color>";

	output += "\n---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------\n";

	// Update display 
	for( int i = 0; i < FRP_LEN ; i ++ )
	{
		output += FRP_LINES[ (FRP_MAX + FRP_PTR - FRP_LEN + i) % FRP_MAX ];
	}

	ltext.text = output;
}

#endif

}

using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace AtE {

	public static partial class Globals {

		public static void ImGui_Object(string label, object value, HashSet<int> seen) {
			string nextLabel = "No Label";
			if( value == null ) {
				ImGui.Text($"{label} = null");
				return;
			}
			Type type = value.GetType();
			int hash = value.GetHashCode();
			if ( (!type.IsValueType)
				&& seen.Contains(hash) ) {
				ImGui.BulletText($"*dup* {label} = {type.Name} hash {hash}");
				// label = "*cycle* " + label;
				return;
			}
			seen.Add(hash);
			if ( type.IsArray ) {
				object[] array = (object[])value;
				for ( int i = 0; i < array.Length; i++ ) {
					var item = array[i];
					nextLabel = $"{label}[{i}]";
					if( ImGui_ObjectLabel(nextLabel, item, seen) ) {
						ImGui_Object($"[{i}]", item, seen);
						ImGui_ObjectLabelPop();
					}
				}
				return;
			}

			if( type.GetInterface("IDictionary") != null ) {
				var dict = (System.Collections.IDictionary)value;
				int itemCount = 0;
				foreach(object key in dict.Keys) {
					if( itemCount < 12 ) {
						nextLabel = $"[{key}]";
						var item = dict[key];
						if( ImGui_ObjectLabel(nextLabel, item, seen) ) {
							ImGui_Object(nextLabel, item, seen);
							ImGui_ObjectLabelPop();
						}

					}
					itemCount++;
				}
				return;
			}

			if( type.GetInterface("IEnumerable") != null ) {
				int itemCount = 0;
				// not the generic interface, so we get only objects
				foreach ( object item in (System.Collections.IEnumerable)value ) {
					if ( itemCount < 12 ) { // TODO: skip and/or scroll?
						nextLabel = $"[{itemCount}]";
						if( ImGui_ObjectLabel(nextLabel, item, seen) ) {
							ImGui_Object(nextLabel, item, seen);
							ImGui_ObjectLabelPop();
						}
					}
					itemCount++;
				}
				ImGui.Text($"Item Count: {itemCount}");
				return;
			}

			var props = type.GetProperties()
					.Select(p => (p.Name, p.PropertyType.Name, p.GetValue(value)));
			var fields = type.GetFields()
					.Select(f => (f.Name, f.FieldType.Name, f.GetValue(value)));

			foreach( var field in fields.Concat(props).OrderBy(x => x.Item2) ) {
				if ( ImGui_ObjectLabel(field.Item1, field.Item3, seen) ) {
					ImGui_Object(field.Item1, field.Item3, seen);
					ImGui_ObjectLabelPop();
				}
			}
			foreach ( var method in type.GetMethods() ) {
				if ( (method?.Name.StartsWith("Get") ?? false)
					&& method.GetParameters().Length == 0
					&& !method.Name.Equals("GetType")
					&& !method.IsGenericMethod
					&& method.ReturnType != typeof(void) ) {
					// ImGui.Text("method " + method.Name);
					// ImGui_BrowseObjectField(method.Name +"()", method.ReturnType, method.Invoke(obj, new object[] { }), seen);
					var v = method.Invoke(value, new object[] { });
					if ( ImGui_ObjectLabel(method.Name + "()", v, seen) ) {
						ImGui_Object(method.Name, v, seen);
						ImGui_ObjectLabelPop();
					}
				}
			}
		}

		public static bool ImGui_ObjectLabel(string label, object value, HashSet<int> seen) {
			ImGui.AlignTextToFramePadding();
			if ( value == null ) {
				ImGui.BulletText($"{label} = null");
				return false;
			}
			Type type = value.GetType();
		

			if ( type == typeof(IntPtr) ) {
				ImGui.AlignTextToFramePadding();
				ImGui.BulletText($"{label}"); ImGui.SameLine(0f, 2f);
				ImGui_Address((IntPtr)value, "@");
				return false;
			}
			
			if ( type.IsPrimitive ) {
				ImGui.BulletText($"{label} = {type.Name} {value}");
				return false; 
			}

			if ( type.IsEnum ) {
				ImGui.BulletText($"{label} = enum {value}");
				return false;
			}

			if ( type.IsArray ) {
				object[] array = (object[])value;
				return ImGui.TreeNode($"{label} [{array.Length} items]");
			}

			if ( type.Equals(typeof(ArrayHandle)) ) {
				ArrayHandle handle = (ArrayHandle)value;
				return ImGui.TreeNode($"{label} = HeadToTail -> 0x{handle.Head.ToInt64():X}");
			}

			if (type.Equals(typeof(ActorSkill))) {
				var skill = (ActorSkill)value;
				return ImGui.TreeNode($"{label} = ActorSkill {skill.DisplayName} {skill.CurVaalSouls}/{skill.MaxVaalSouls}");
			}

			/*
			if ( type.Equals(typeof(EntityListNode)) ) {
				var node = (EntityListNode)value;
				var nodeId = $"{(long)node.Address:X}".Last(4);
				return ImGui.TreeNode($"- {label} node {nodeId} {(IsValid(node) ? "[ent]" : "[no ent]")}");
			}
			*/

			if ( type.Equals(typeof(string)) ) {
				ImGui.BulletText($"{label} = string \"{(string)value}\"");
				return false;
			}

			if ( type.GetInterface("IDictionary") != null) {
				return ImGui.TreeNode($"[D] {label}");
			}

			if ( type.GetInterface("IEnumerable") != null ) {
				return ImGui.TreeNode($"[+] {label}");
			}

			if ( type.Equals(typeof(Element)) ) {
				var elem = (Element)value;
				var elemId = $"{(long)elem.Address:X}".Last(4);
				var r = elem.GetClientRect();
				string nextLabel = $"{label} {type.Name} {elemId} {(IsValid(elem) ? "Valid" : "Invalid")} <{r.X},{r.Y},{r.Width}x{r.Height}>";
				if ( ImGui.Button($">##{label}") ) {
					Run_ObjectBrowser(nextLabel, value);
				}
				ImGui.SameLine();
				if( ImGui.TreeNode(nextLabel) ) {
					return true;
				}
				// only if its not toggled open, hover should highlight the element's rect
				if( IsValid(elem) && ImGui.IsItemHovered() && (r.Width * r.Height) > 0) {
					DrawFrame(r, Color.Yellow, 3);
				}
				return false;
			}

			if ( type.Equals(typeof(Entity)) || type.IsSubclassOf(typeof(Entity)) ) {
				var ent = (Entity)value;
				return ImGui.TreeNode($"{label} [{(IsValid(ent) ? "Valid" : "Invalid")}]");
			}

			if (type.Equals(typeof(Vector2)) ) {
				Vector2 v = (Vector2)value;
				ImGui.BulletText($"{label} = Vector2 <{v.X},{v.Y}>");
				return false;
			}
			
			if (type.Equals(typeof(Vector3)) ) { 
				Vector3 v = (Vector3)value;
				ImGui.BulletText($"{label} = Vector3 <{v.X},{v.Y},{v.Z}>");
				return false;
			}
			
			if ( type.Equals(typeof(Vector4)) ) {
				Vector4 v = (Vector4)value;
				ImGui.BulletText($"{label} = Vector4 <{v.X},{v.Y},{v.Z},{v.W}>");
				return false;
			}

			if ( type.Equals(typeof(RectangleF)) ) {
				RectangleF r = (RectangleF)value;
				ImGui.BulletText($"{label} = RectangleF <{r.X},{r.Y},{r.Width}x{r.Height}>");
				if( ImGui.IsItemHovered() ) {
					DrawFrame(r, Color.Yellow, 3);
				}
				return false;
			}

			if ( type.Equals(typeof(Matrix4x4)) ) {
				var v = (Matrix4x4)value;
				ImGui.BulletText($"{label} = Matrix4x4 <{v.M11:F4},{v.M12:F4},{v.M13:F4},{v.M14:F4}>");
				ImGui.BulletText($"{label} = Matrix4x4 <{v.M21:F4},{v.M22:F4},{v.M23:F4},{v.M24:F4}>");
				ImGui.BulletText($"{label} = Matrix4x4 <{v.M31:F4},{v.M32:F4},{v.M33:F4},{v.M34:F4}>");
				ImGui.BulletText($"{label} = Matrix4x4 <{v.M41:F4},{v.M42:F4},{v.M43:F4},{v.M44:F4}>");
				return false;
			}

			return ImGui.TreeNode($"{label} as {type.Name}");

		}

		public static void ImGui_ObjectLabelPop() {
			ImGui.TreePop();
		}


		public static void Run_ObjectBrowser(string label, object value) {
			Type type = value?.GetType();
			bool open = true;
			Run(label, (self, dt) => {
				ImGui.SetNextWindowSize(new Vector2(300, 200), ImGuiCond.FirstUseEver);
				if ( ImGui.Begin(label, ref open) ) {
					try {
						ImGui_Object(label, value, new HashSet<int>());
					} catch ( Exception e ) {
						ImGui.PushStyleColor(ImGuiCol.Text, (uint)ToRBGA(Color.Red));
						ImGui.Text(e.Message);
						ImGui.TextWrapped(e.StackTrace);
						ImGui.PopStyleColor();
						Log(e.Message);
						Log(e.StackTrace);
					} finally {
						ImGui.End();
					}
				}
				return open ? self : null;
			});
		}

	}
}

using UnityEngine;
using System;
using System.Collections.Generic;

namespace LegacyUtility
{
    [AttributeUsage(AttributeTargets.Class)]
    public class ManagerDefaultPrefabAttribute : Attribute
    {
        private string m_Prefab;

        public string prefab => m_Prefab;

        public ManagerDefaultPrefabAttribute(string prefabName)
        {
            m_Prefab = prefabName;
        }
    }

    public static class TypeUtility
    {
        public static Type[] GetConcreteTypes<T>()
        {
            List<Type> types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] assemblyTypes = null;

                try
                {
                    assemblyTypes = assembly.GetTypes();
                }
                catch
                {
                    Debug.LogError($"Could not load types from assembly : {assembly.FullName}");
                }

                if (assemblyTypes != null)
                {
                    foreach (Type t in assemblyTypes)
                    {
                        if (typeof(T).IsAssignableFrom(t) && !t.IsAbstract)
                        {
                            types.Add(t);
                        }
                    }
                }

            }
            return types.ToArray();
        }
    }


    public abstract class Manager : MonoBehaviour
    {
        private static Dictionary<Type, Manager> s_Managers;

        private static readonly Type[] kAllManagerTypes = TypeUtility.GetConcreteTypes<Manager>();

        private static T GetCustomAttribute<T>(Type type) where T : Attribute
        {
            object[] attributes = type.GetCustomAttributes(typeof(T), true);

            if (attributes != null && attributes.Length > 0)
            {
                return (T)attributes[0];
            }
            else
            {
                return null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoCreateAll()
        {
            s_Managers = new();

            Type[] array = kAllManagerTypes;
            foreach (Type type in array)
            {
                Debug.Log("Manager : " + type.Name + " is being created");
                ManagerDefaultPrefabAttribute customAttribute = GetCustomAttribute<ManagerDefaultPrefabAttribute>(type);
                GameObject gameObject2;
                if (customAttribute != null)
                {
                    GameObject gameObject = Resources.Load<GameObject>(customAttribute.prefab);
                    if (gameObject == null)
                    {
                        gameObject = Resources.Load<GameObject>("Default_" + customAttribute.prefab);
                    }

                    if (!(gameObject != null))
                    {
                        Debug.LogError("Could not instantiate default prefab for " + type.ToString() + " : No prefab '" + customAttribute.prefab + "' found in resources folders. Ignoring...");
                        continue;
                    }

                    gameObject2 = Instantiate(gameObject);
                }
                else
                {
                    gameObject2 = new GameObject();
                    gameObject2.AddComponent(type);
                }

                gameObject2.name = type.Name;
                DontDestroyOnLoad(gameObject2);
                Manager value = (Manager)gameObject2.GetComponent(type);
                s_Managers.Add(type, value);
            }
        }
    }
}

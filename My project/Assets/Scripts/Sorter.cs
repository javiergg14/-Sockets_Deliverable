using System.Collections.Generic;
using UnityEngine;
using System.Threading;

public class Sorter : MonoBehaviour
{
    public GameObject prefab;
    public bool useQuickSort = true;

    private float[] array;
    private List<GameObject> mainObjects;
    private Thread sortThread;
    private bool heightsDirty = true;
    private bool stopSorting = false;
    private readonly object arrayLock = new object();

    void Start()
    {
        mainObjects = new List<GameObject>();
        array = new float[30000];

        for (int i = 0; i < 30000; i++)
        {
            array[i] = Random.Range(0f, 9.99f);
        }

        logArray();
        spawnObjs();

        if (useQuickSort)
        {
            sortThread = new Thread(() => QuickSort(0, array.Length - 1));
        }
        else
        {
            sortThread = new Thread(BubbleSort);
        }

        sortThread.Start();
    }

    void logArray()
    {
        Debug.Log($"Array generado. Total de elementos: {array.Length}");
    }

    void spawnObjs()
    {
        for (int i = 0; i < array.Length; i++)
        {
            GameObject obj = Instantiate(prefab, new Vector3((float)i / 1000f, transform.position.y, 0), Quaternion.identity);
            mainObjects.Add(obj);
        }
    }

    bool updateHeights()
    {
        bool changed = false;
        for (int i = 0; i < mainObjects.Count; i++)
        {
            float targetHeight;
            lock (arrayLock)
            {
                targetHeight = array[i];
            }

            Vector3 currentScale = mainObjects[i].transform.localScale;
            if (Mathf.Abs(currentScale.y - targetHeight) > 0.001f)
            {
                mainObjects[i].transform.localScale = new Vector3(currentScale.x, targetHeight, currentScale.z);
                changed = true;
            }
        }
        return changed;
    }

    void BubbleSort()
    {
        int n = array.Length;
        bool swapped;
        for (int i = 0; i < n - 1; i++)
        {
            if (stopSorting) return;
            swapped = false;
            for (int j = 0; j < n - i - 1; j++)
            {
                bool needsSwap = false;
                lock (arrayLock)
                {
                    if (array[j] > array[j + 1])
                    {
                        (array[j], array[j + 1]) = (array[j + 1], array[j]);
                        needsSwap = true;
                    }
                }
                if (needsSwap) swapped = true;
            }
            if (!swapped) break;
        }
    }

    void QuickSort(int low, int high)
    {
        if (stopSorting) return;
        if (low < high)
        {
            int pi = Partition(low, high);
            QuickSort(low, pi - 1);
            QuickSort(pi + 1, high);
        }
    }

    int Partition(int low, int high)
    {
        float pivot;
        lock (arrayLock) { pivot = array[high]; }

        int i = (low - 1);
        for (int j = low; j < high; j++)
        {
            if (stopSorting) return i + 1;

            lock (arrayLock)
            {
                if (array[j] < pivot)
                {
                    i++;
                    (array[i], array[j]) = (array[j], array[i]);
                }
            }
        }

        lock (arrayLock)
        {
            (array[i + 1], array[high]) = (array[high], array[i + 1]);
        }
        return i + 1;
    }

    void Update()
    {
        if (heightsDirty)
        {
            bool changed = updateHeights();
            if (!changed && !sortThread.IsAlive)
            {
                heightsDirty = false;
                Debug.Log("Sorting finished, heights no longer updated");
            }
        }
    }

    void OnDestroy()
    {
        stopSorting = true;
        if (sortThread != null && sortThread.IsAlive)
        {
            sortThread.Join(500);
        }
    }
}
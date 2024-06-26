using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEditor;
using UnityEditor.U2D.Aseprite;
using UnityEngine;
using UnityEngine.UIElements;

public class SimulationController : MonoBehaviour
{
    // Boundary
    public Vector2 BoundarySize = new Vector2(20, 20);
    public Vector2 SpawnCenter = new Vector2(5,5);
    public float SpawnArea = 1f; 

    // External forces
    public float Gravity = -9.8f;


    // Fluid
    public float CellSize;
    private int numCellsX;
    private int numCellsY;
    private float h;
    private int numCells;

    private CellType[] cellTypes;
    private Vector2[] _cellVel;
    private Vector2[] _celldVel;
    private Vector2[] _cellPrevVel;
    private int[] _numCellParticles; // number of particles in each cell
    private int[] _numCellParticlesDenseGrid;
    private float[] p;
    private float[] s;


    // Particles
    public int NumParticles = 50;
    /// GameObject to be assigned from editor
    public int PushIterations = 3;
    public int PressureIterations = 100;
    public float Density = 1000;
    public float GtoPRelaxation = 1;
    public float DensityStifness = 1;
    public float OverRelaxation = 1.9f;
    public float FLIPRatio = 1;
    [SerializeField]
    public GameObject ParticleGameObject;
    /// GameObject array to loop through the particles
    private float _particleRadius;
    private GameObject[] _particleGameObject;
    private Vector2[] _particlePos;
    private Vector2[] _particleVel;
    private int[] _cellParticlesIds;

    void Awake()
    {
        // Fluid
        numCellsX = (int)(Math.Floor(BoundarySize.x/CellSize)+1);
        numCellsY = (int)(Math.Floor(BoundarySize.y/CellSize)+1);
        h = Math.Max(BoundarySize.x/numCellsX, BoundarySize.y/numCellsY);
        numCells = numCellsX*numCellsY;
        cellTypes = new CellType[numCells];
        _cellVel = new Vector2[numCells];
        _celldVel = new Vector2[numCells];
        _numCellParticles = new int[numCells];
        _numCellParticlesDenseGrid = new int[numCells+1];
        _cellPrevVel = new Vector2[numCells];
        p = new float[numCells];
        s = new float[numCells];


        // Particles
        _particleGameObject = new GameObject[NumParticles];
        _particlePos = new Vector2[NumParticles];
        _particleVel = new Vector2[NumParticles];
        _cellParticlesIds = new int[NumParticles];
    }

    void Start(){
        SpawnParticles();
        PushParticlesApart();
    }

    // Update is called once per frame
    void Update()
    {
        IntegrateParticles();
        PushParticlesApart();
        TransferVelocities(true);
        SolveIncompressibility(PressureIterations, OverRelaxation);
        TransferVelocities(false, FLIPRatio: Math.Clamp(FLIPRatio, 0, 1));
    }



    void IntegrateParticles(){
        for (int i = 0; i < NumParticles; i++){
            _particleVel[i].y += Gravity * Time.deltaTime;
            _particlePos[i] += _particleVel[i] * Time.deltaTime;
            HandleBoundaryCollisions(i);

            _particleGameObject[i].transform.position = _particlePos[i];
        }
    }

    void PushParticlesApart() {
        float minDist = 2.0f * _particleRadius;
        float minDist2 = minDist * minDist;

        // Step 1: Count particles per cell
        Array.Clear(_numCellParticles, 0, _numCellParticles.Length);
        for (int i = 0; i < NumParticles; i++){
            Vector2 pos = _particlePos[i];
            int cellNr = findParticleCellNr(pos);
            _numCellParticles[cellNr]++;
        }

        // Step 2: Accumulated sums
        int val = 0;
        for (int i = 0; i<_numCellParticles.Length; i++){
            val += _numCellParticles[i];
            _numCellParticlesDenseGrid[i] = val;
        }
        _numCellParticlesDenseGrid[numCells] = val; // guard entry in the dense grid
        
        // Step 3: fill the particles into the cells
        for (int i = 0; i<NumParticles; i++) {
            Vector2 pos = _particlePos[i];
            int cellNr = findParticleCellNr(pos);
            _numCellParticlesDenseGrid[cellNr]--;
            _cellParticlesIds[_numCellParticlesDenseGrid[cellNr]] = i;
        }

        // Step 4: push particles apart
        for (int iter = 0; iter < PushIterations; iter++){

            for (int i = 0; i < NumParticles; i++){
                Vector2 pos = _particlePos[i];
                int pxi = Mathf.FloorToInt(pos.x/h);
                int pyi = Mathf.FloorToInt(pos.y/h);

                int x0 = Math.Clamp(pxi-1, 0, numCellsX-1);
                int x1 = Math.Clamp(pxi+1, 0, numCellsX-1);
                int y0 = Math.Clamp(pyi-1, 0, numCellsX-1);
                int y1 = Math.Clamp(pyi+1, 0, numCellsX-1);


                for (int xi = x0; xi <= x1; xi++)
                {
                    for (int yi = y0; yi <= y1; yi++)
                    {
                        int cellNr = xi*numCellsY + yi;
                        int firstParticle = _numCellParticlesDenseGrid[cellNr];
                        int lastParticle = _numCellParticlesDenseGrid[cellNr+1];
                        for (int particle = firstParticle; particle < lastParticle; particle++)
                        {
                            int id = _cellParticlesIds[particle];
                            if (id == i) continue;

                            Vector2 qPos = _particlePos[id];
                            Vector2 diff = qPos - pos;
                            float dist2 = diff.sqrMagnitude;
                            if (dist2 > minDist2 || dist2 == 0.0f) continue;

                            float dist = Mathf.Sqrt(dist2);

                            float s = (minDist -  dist)/dist;
                            Vector2 displacement = diff * s;
                            _particlePos[i] -= displacement;
                            _particlePos[id] += displacement;
                        }
                    }
                }


            }
        }

    }

    private int findParticleCellNr(Vector2 pos){
            int xi = Math.Clamp(Mathf.FloorToInt(pos.x / h), 0, numCellsX-1); // x coordinate of the cell
            int yi = Math.Clamp(Mathf.FloorToInt(pos.y / h), 0, numCellsY-1); // y coordinate of the cell
            int cellNr = xi*numCellsY + yi; // index of the cell in the flattened array

            return cellNr;       
    }


    void HandleBoundaryCollisions(int i)
    {
        if (_particlePos[i].x >= BoundarySize.x / 2)
        {
            _particleVel[i].x = 0.0f; //-0.2f*_particleVel[i].x;
            // Make sure the particle is within bounds after bounce
            _particlePos[i].x = BoundarySize.x / 2;
        }

        if (_particlePos[i].x <= -BoundarySize.x/2){
            _particleVel[i].x = 0.0f; //-0.2f*_particleVel[i].x;
            _particlePos[i].x = -BoundarySize.x / 2;
        }

        if (_particlePos[i].y >= BoundarySize.y / 2)
        {
            _particleVel[i].y = 0.0f; //-0.2f*_particleVel[i].y;
            // Make sure the particle is within bounds after bounce
            _particlePos[i].y = BoundarySize.y / 2;
        }

        if (_particlePos[i].y <= -BoundarySize.y / 2)
        {
            _particleVel[i].y = 0.0f; //-0.2f*_particleVel[i].y;
            // Make sure the particle is within bounds after bounce
            _particlePos[i].y = -BoundarySize.y / 2;
        }
    }


    void TransferVelocities(bool toGrid, float FLIPRatio = 0.1f) {
        var h2 = h / 2;

        if (toGrid) {
            // Initialize cell types to air
            for (int i = 0; i < numCells; i++) {
                cellTypes[i] = CellType.Air;
            }

            // Save previous velocities
            Array.Copy(_cellVel, _cellPrevVel, _cellVel.Length);

            // Reset current velocities and weights
            Array.Clear(_cellVel, 0, _cellVel.Length);
            Array.Clear(_celldVel, 0, _celldVel.Length);

            // Identify fluid cells
            for (int i = 0; i < NumParticles; i++) {
                var pos = _particlePos[i];
                int xi = Mathf.Clamp((int)((pos.x + BoundarySize.x / 2) / h), 0, numCellsX - 1);
                int yi = Mathf.Clamp((int)((pos.y + BoundarySize.y / 2) / h), 0, numCellsY - 1);
                int cellNr = xi * numCellsY + yi;
                if (cellTypes[cellNr] == CellType.Air) cellTypes[cellNr] = CellType.Fluid;
            }

            // Transfer particle velocities to grid
            for (int i = 0; i < NumParticles; i++) {
                var pos = _particlePos[i];
                int Xc = Mathf.Clamp((int)Math.Floor((pos.x + BoundarySize.x / 2) / h), 0, numCellsX - 1);
                int Yc = Mathf.Clamp((int)Math.Floor((pos.y + BoundarySize.y / 2) / h), 0, numCellsY - 1);


                float deltaX = Math.Clamp(Math.Abs((pos.x+BoundarySize.x/2) - Xc*h), 0, 0.98f);
                float deltaY = Math.Clamp(Math.Abs((pos.y+BoundarySize.y/2) - Yc*h), 0, 0.98f);

                float w0 = (1 - deltaX / h) * (1 - deltaY / h);
                float w1 = (deltaX / h) * (1 - deltaY / h);
                float w2 = (1 - deltaX / h) * (deltaY / h);
                float w3 = (deltaX / h) * (deltaY / h);

                int cellNr0 = getCellNrFromCoord(Xc, Yc);
                int cellNr1 = getCellNrFromCoord(Xc + 1, Yc);
                int cellNr2 = getCellNrFromCoord(Xc, Yc + 1);
                int cellNr3 = getCellNrFromCoord(Xc + 1, Yc + 1);

                _cellVel[cellNr0] += _particleVel[i] * w0;
                _cellVel[cellNr1] += _particleVel[i] * w1;
                _cellVel[cellNr2] += _particleVel[i] * w2;
                _cellVel[cellNr3] += _particleVel[i] * w3;

                _celldVel[cellNr0] += new Vector2(w0, w0);
                _celldVel[cellNr1] += new Vector2(w1, w1);
                _celldVel[cellNr2] += new Vector2(w2, w2);
                _celldVel[cellNr3] += new Vector2(w3, w3);
            }

            // Normalize velocities
            for (int i = 0; i < numCells; i++) {
                if (_celldVel[i].x > 0) _cellVel[i].x /= _celldVel[i].x;
                if (_celldVel[i].y > 0) _cellVel[i].y /= _celldVel[i].y;
            }
        } else {
            // Transfer grid velocities to particles
            for (int i = 0; i < NumParticles; i++) {
                var pos = _particlePos[i];
                int Xc = Mathf.Clamp((int)Math.Floor((pos.x + BoundarySize.x / 2) / h), 0, numCellsX - 1);
                int Yc = Mathf.Clamp((int)Math.Floor((pos.y + BoundarySize.y / 2) / h), 0, numCellsY - 1);

                float deltaX = Math.Clamp(Math.Abs((pos.x+BoundarySize.x/2) - Xc*h), 0, 0.98f);
                float deltaY = Math.Clamp(Math.Abs((pos.y+BoundarySize.y/2) - Yc*h), 0, 0.98f);

                float w0 = (1 - deltaX / h) * (1 - deltaY / h);
                float w1 = (deltaX / h) * (1 - deltaY / h);
                float w2 = (1 - deltaX / h) * (deltaY / h);
                float w3 = (deltaX / h) * (deltaY / h);

                int cellNr0 = getCellNrFromCoord(Xc, Yc);
                int cellNr1 = getCellNrFromCoord(Xc + 1, Yc);
                int cellNr2 = getCellNrFromCoord(Xc, Yc + 1);
                int cellNr3 = getCellNrFromCoord(Xc + 1, Yc + 1);

                Vector2 isValidNr0 = checkIfCellIsValid(Xc, Yc);
                Vector2 isValidNr1 = checkIfCellIsValid(Xc + 1, Yc);
                Vector2 isValidNr2 = checkIfCellIsValid(Xc, Yc + 1);
                Vector2 isValidNr3 = checkIfCellIsValid(Xc + 1, Yc + 1);

                float denominator = isValidNr0.x*w0 + isValidNr1.x*w1 + isValidNr2.x*w2 + isValidNr3.x*w3;

                if (denominator > 0) {
                    denominator *= GtoPRelaxation;
                    Vector2 picVel = (isValidNr0 * _cellVel[cellNr0] * w0 + isValidNr1 * _cellVel[cellNr1] * w1 + isValidNr2 * _cellVel[cellNr2] * w2 + isValidNr3 * _cellVel[cellNr3] * w3)/denominator;
                    Vector2 flipVel = _particleVel[i] + 
                                        (isValidNr0 * (_cellVel[cellNr0] - _cellPrevVel[cellNr0]) * w0 +
                                        isValidNr1 * (_cellVel[cellNr1] - _cellPrevVel[cellNr1]) * w1 +
                                        isValidNr2 * (_cellVel[cellNr2] - _cellPrevVel[cellNr2]) * w2 +
                                        isValidNr3 * (_cellVel[cellNr3] - _cellPrevVel[cellNr3]) * w3)/denominator;

                    var prevVel = _particleVel[i];
                    _particleVel[i] = FLIPRatio * flipVel + (1 - FLIPRatio) * picVel;

                    if (_particleVel[i].y > 20){
                        print((w0,w1,w2,w3, deltaX/h ,deltaY/h));
                        print((_particleVel[i].y, prevVel.y, _cellVel[cellNr0].y, _cellVel[cellNr1].y, _cellVel[cellNr2].y, _cellVel[cellNr3].y));
                        throw new Exception(); 
                    }
                }
            }
        }
    }

    private int getCellNrFromCoord(int i, int j) {
        return Math.Clamp(i * numCellsY + j, 0, numCells - 1);
    }

    private Vector2 checkIfCellIsValid(int xc, int yc, bool isAirValid=false) {
        if (xc < 0 || yc < 0 || xc >= numCellsX || yc >= numCellsY) return Vector2.zero;
        int cellNr = getCellNrFromCoord(xc, yc);
        if (isAirValid){
            return Vector2.one;
        } else {
            return cellTypes[cellNr] == CellType.Air ? Vector2.zero : Vector2.one;
        }
    }


    void SolveIncompressibility(int numIters, float overRelaxation)
    {
        int n = numCellsY;
        float cp = Density * h / Time.deltaTime;  // Assuming density is 1 for simplicity, adjust if needed

        // Initialize pressure
        Array.Clear(p, 0, p.Length);

        // Save previous velocities
        Array.Copy(_cellVel, _cellPrevVel, _cellVel.Length);

        // Iterate to solve for pressure
        for (int iter = 0; iter < numIters; iter++)
        {
            for (int i = 1; i < numCellsX - 1; i++)
            {
                for (int j = 1; j < numCellsY - 1; j++)
                {
                    int center = i * n + j;
                    if (cellTypes[center] != CellType.Fluid)
                        continue;

                    int left = (i - 1) * n + j;
                    int right = (i + 1) * n + j;
                    int bottom = i * n + (j - 1);
                    int top = i * n + (j + 1);

                    float isValidLeft = checkIfCellIsValid(i-1, j, isAirValid:true).x;
                    float isValidRight = checkIfCellIsValid(i+1, j, isAirValid:true).x;
                    float isValidTop = checkIfCellIsValid(i, j+1, isAirValid:true).y;
                    float isValidBot = checkIfCellIsValid(i, j-1, isAirValid:true).y;

                    float div = isValidRight*_cellVel[right].x - isValidLeft*_cellVel[center].x + isValidTop*_cellVel[top].y - isValidBot*_cellVel[center].y;
                    float denominator = isValidLeft+isValidRight+isValidTop+isValidBot;

                    if (denominator > 0){
                        float newPressure = -div / denominator;

                        newPressure *= overRelaxation;
                        p[center] += cp * newPressure;

                        _cellVel[center].x -= newPressure;
                        _cellVel[right].x += newPressure;
                        _cellVel[center].y -= newPressure;
                        _cellVel[top].y += newPressure;
                    }

                }
            }
        }

    }




    void SpawnParticles()
    {

        for (int i = 0; i < NumParticles; i++)
        {
            Vector2 spawnPosition = SpawnCenter + UnityEngine.Random.insideUnitCircle*SpawnArea;
            GameObject particle = Instantiate(ParticleGameObject, spawnPosition, Quaternion.identity);
            _particleRadius = particle.transform.localScale.x/2;
            _particleGameObject[i] = particle;
            _particlePos[i] = spawnPosition;
            _particleVel[i] = Vector2.zero;
        }
    }

    void OnDrawGizmos() {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector2.zero, BoundarySize);

        if (!Application.isPlaying) {
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(SpawnCenter, SpawnArea);
        } else {
            // Draw grid cells
            for (int i = 0; i < numCellsX; i++) {
                for (int j = 0; j < numCellsY; j++) {
                    int cellIndex = i * numCellsY + j;

                    // Calculate cell position
                    float xPos = i * h - BoundarySize.x / 2 + h / 2;
                    float yPos = j * h - BoundarySize.y / 2 + h / 2;
                    Vector2 cellPos = new Vector2(xPos, yPos);

                    // Calculate color based on velocity magnitude
                    float velocityMagnitude = _cellVel[cellIndex].magnitude;
                    Color cellColor = Color.Lerp(Color.blue, Color.red, velocityMagnitude / 10f);
                    Gizmos.color = cellColor.WithAlpha(0.4f);
                    // Draw the cell
                    Gizmos.DrawCube(cellPos, new Vector3(h, h, 0));
                    
                    // Draw velocity text
                    //Handles.color = Color.white;
                    GUIStyle style = new GUIStyle();
                    style.fontSize = 11;
                    style.normal.textColor = Color.white;
                    Handles.Label(cellPos, ((int)velocityMagnitude).ToString(), style);

                    // Draw velocity vector
                    // Vector2 velocity = _cellVel[cellIndex].normalized/3;
                    // Vector3 endPos = new Vector3(cellPos.x + velocity.x, cellPos.y + velocity.y, 0);
                    // Gizmos.color = Color.white;
                    // DrawArrow(new Vector3(cellPos.x, cellPos.y, 0), endPos);

                }
            }

            void DrawArrow(Vector3 start, Vector3 end) {
                Gizmos.DrawLine(start, end);
                
                Vector3 direction = (end - start).normalized;
                Vector3 right = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 180 + 20, 0) * new Vector3(0, 0, 1);
                Vector3 left = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 180 - 20, 0) * new Vector3(0, 0, 1);

                Gizmos.DrawLine(end, end + right * 0.1f);
                Gizmos.DrawLine(end, end + left * 0.1f);
            }
        }
    }



}

public enum CellType{
    Solid,
    Fluid,
    Air
}


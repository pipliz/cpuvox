# cpuvox
A C# implementation of voxlap-style rendering, using Unity and their Burst compiler

Implementation mostly based on the paper named "Research on Improving Methods for Visualizing Common Elements in Video Game Applications" by Sven Forstmann, which can be found here: http://svenforstmann.com/pdf/Ph.D.Thesis.Sven.Forstmann.pdf (page 84 and on)

Summary of the implemented algorithm:

- Compute the 'vanishing point' (VP), where the near plane intersects with the player Y (vertical) line
- Divide the screen into 4 sections when the VP is visible (looking down/up-ish), where each section is a quarter of the world in XZ space
- Get a raybuffer texture, where we can render our semi-2d semi-3d results of phase 1 to (one column per ray)

Phase 1:
1) Make a bitmask to keep track of which pixels we have written to in this raybuffer column
2) Cast a ray through the XZ world for every column of pixels in each segment, drawing into the ray-buffer
3) For every XZ world column of voxels:

3.1) Adjust the LOD level & world we trace through based on distance

3.2) Define a quad 'Q' that is the intersection of the XZ ray with the maximum voxel column at this position

3.3) Project the 4 corners of Q to homogeneous camera space

3.4) Frustum cull these 2 lines (the 'near' and the 'far' vertical lines Q) to find a min/max world Y of the voxels to render - if fully culled, early out to skybox

3.5) Project these to the screen to find a min/max pixel we may write to for this column - if it does not overlap our written pixel bounds, early out to skybox 

3.6) Iterate the run-length compressed list of solid voxels; top->bottom if the camera points down, bottom->top if the camera points up (to maintain front-to-back rendering)

3.7) For every voxel run in the column:

3.7.1) Check if it is within the desired world bounds (if not, early out)

3.7.2) Interpolate the projected corners of Q to get the homogeneous camera space coordinates of the voxel run corners that we need

3.7.3) Draw the side/top/bottom lines; since we draw voxel runs in one go, use perspective correct texturing for coloring the side

3.7.4) Adjust the written pixel bounds based on the pixel we draw here - if min >= max we won't write more pixels, early out to skybox; The bitmask is essential here to extend the written pixel bounds to be narrower after we've closed a 'gap' in the column

3.8) Adjust the frustum to be narrower based on the written pixel bounds

4) Write the skybox to any pixel in the raybuffer column we didn't write to

Phase 2:
Project the semi-2d semi-3d raybuffer to the screen, making it full 3d

Documentation/hints/general stuff about this project:
- The main segment setup code is in Assets/Code/RenderManager.cs
- The main raybuffer rendering code is in Assets/Code/Rendering/DrawSegmentRayJob.cs
- The raybuffer texture is divided into smaller textures and assembled into a full texture because of 2 reasons:
-- 1) we don't want to upload the entire texture every frame (it can be 10-20 MB+, it's bigger than a 4-byte-per-pixel buffer)
-- 2) this means we can start uploading the first textures on the main thread when they've been drawn to before we are fully done drawing.
- There's lots of pointers everywhere because Burst does not (currently) support passing NativeArray's to functions at the point of writing (as they are considered managed objects). We can't use IJobs - which do support it - because we want to wake the main thread and tightly control scheduling around that
- Models are not included in the git due to licensing concerns and because it's large binary data
- There's no editing support because that was not the point of this project (it was to get the algorithm working and decently performant), there's no complex acceleration structure that prevents realtime edits
- Writing a semi-decent multithreaded triangle mesh voxelizer was suprisingly hard
- Special note about the algorithm losing a lot of precision when looking horizontal-ish if you don't clamp the segment triangles to a tight fit around the screen, which was not mentioned in the paper
- The raybuffer is ARGB32 instead of RGB24 because it's assembled into a rendertexture which does not support 24-bit colors in unity - possibly we can work around this some other way to boost performance a bit, but it's a bit involved.
- There's no depth buffer
- The .obj importer only supports one model consisting only out of triangles and vertex colors (including normals/uvs etc will break it)
- The output of the .obj importer is cached into a .dat file which is just a binary copy of the mesh from memory - it helps make things a lot easier (the powerplant.obj source is 800 MB and took 30 secs to parse)
